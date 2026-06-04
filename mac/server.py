# =========================================================
# EPG Mac Recording Server
# /Users/garyscudder/epg/server.py
# Run with: python3 server.py
# =========================================================

import json
import logging
import sqlite3
import subprocess
import threading
import time
from datetime import datetime
from pathlib import Path

from flask import Flask, jsonify, request

app = Flask(__name__)

# --- CONFIG ------------------------------------------------
SERVER_VERSION = "2026.06.04.1"
NAS_MOVIES_PATH = "/Volumes/Plex/Movies"
NAS_TV_PATH = "/Volumes/Plex/TV Shows"
FIRE_TV_PATH = "/Volumes/Fire TV"
FIRE_TV_PATHS = ["/Volumes/Fire TV", "/Volumes/FireTV"]
FFMPEG_PATH = "/opt/homebrew/bin/ffmpeg"
SERVER_PORT = 5000
LOG_PATH = "/Users/garyscudder/epg/logs/server.log"
UPCOMING_JSON_PATH = "/Users/garyscudder/epg/upcoming.json"

# Mount the GarysNas EPG share on the Mac so this path exists.
DB_PATH = "/Volumes/EPG/Movies.db"

# Existing Mac config. Recording stream settings are read from
# app_settings in Movies.db, not from this config file.
CONFIG_PATH = "/Users/garyscudder/epg/config.json"

SCHEDULER_INTERVAL_SECONDS = 15
STATUS_INTERVAL_SECONDS = 30
CLAIM_WINDOW_SECONDS = 60
RECORDING_PADDING_SECONDS = 120
CONVERSION_RECORDING_SAFETY_SECONDS = 15 * 60
MIN_DURATION_RATIO = 0.85
MIN_AVG_BITRATE_KBPS = 1200
# ----------------------------------------------------------

# --- LOGGING ----------------------------------------------
Path(LOG_PATH).parent.mkdir(parents=True, exist_ok=True)
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.FileHandler(LOG_PATH),
        logging.StreamHandler()
    ]
)
log = logging.getLogger(__name__)

# --- JOB STORE --------------------------------------------
jobs = {}
jobs_lock = threading.Lock()
conversion_lock = threading.Lock()
active_conversion = None
conversion_queue = []
conversion_queue_history = []
conversion_queue_lock = threading.Lock()
stop_scheduler = threading.Event()
config_loaded = False


# =========================================================
# CONFIG / DB HELPERS
# =========================================================

def load_config():
    global DB_PATH
    global NAS_MOVIES_PATH, NAS_TV_PATH, FIRE_TV_PATH, FIRE_TV_PATHS, FFMPEG_PATH, SERVER_PORT, UPCOMING_JSON_PATH
    global config_loaded

    path = Path(CONFIG_PATH)
    if not path.exists():
        log.warning(f"Mac config not found at {CONFIG_PATH}; using constants")
        config_loaded = False
        return

    try:
        data = json.loads(path.read_text())

        DB_PATH = first_config_value(data, "DB_PATH", "db_path", "movies_db", "MOVIES_DB", default=DB_PATH)
        NAS_MOVIES_PATH = first_config_value(data, "NAS_MOVIES_PATH", "nas_movies_path", "PLEX_MOVIES_PATH", "plex_movies_path", default=NAS_MOVIES_PATH)
        NAS_TV_PATH = first_config_value(data, "NAS_TV_PATH", "nas_tv_path", "PLEX_TV_PATH", "plex_tv_path", default=NAS_TV_PATH)
        FIRE_TV_PATH = first_config_value(data, "FIRE_TV_PATH", "fire_tv_path", "WAREHOUSE", "warehouse", default=FIRE_TV_PATH)
        if isinstance(data.get("FIRE_TV_PATHS"), list):
            FIRE_TV_PATHS = [str(p) for p in data["FIRE_TV_PATHS"] if p]
        elif FIRE_TV_PATH:
            FIRE_TV_PATHS = [FIRE_TV_PATH, "/Volumes/Fire TV", "/Volumes/FireTV"]
        FIRE_TV_PATHS = list(dict.fromkeys(FIRE_TV_PATHS))
        if NAS_TV_PATH == "/Volumes/Plex/TV Shows" and NAS_MOVIES_PATH:
            movies_path = Path(NAS_MOVIES_PATH)
            if movies_path.name.lower() == "movies":
                NAS_TV_PATH = str(movies_path.parent / "TV Shows")
        FFMPEG_PATH = first_config_value(data, "FFMPEG_PATH", "ffmpeg_path", default=FFMPEG_PATH)
        UPCOMING_JSON_PATH = first_config_value(data, "UPCOMING_JSON_PATH", "upcoming_json_path", default=UPCOMING_JSON_PATH)

        port = first_config_value(data, "SERVER_PORT", "server_port", "MAC_PORT", "mac_port", default=SERVER_PORT)
        SERVER_PORT = int(port)
        config_loaded = True
    except Exception as e:
        config_loaded = False
        log.error(f"Could not load config {CONFIG_PATH}: {e}")


def first_config_value(data: dict, *keys, default=""):
    for key in keys:
        if key in data and data[key] not in (None, ""):
            return data[key]
    return default


def db_connect():
    con = sqlite3.connect(DB_PATH, timeout=30)
    con.row_factory = sqlite3.Row
    con.execute("PRAGMA busy_timeout=30000")
    return con


def is_sqlite_locked_error(err: Exception) -> bool:
    return isinstance(err, sqlite3.OperationalError) and "database is locked" in str(err).lower()


def parse_db_time(value: str) -> datetime:
    return datetime.strptime(value, "%Y-%m-%d %H:%M:%S")


def build_stream_url(stream_id: str) -> str:
    settings = load_app_settings("recording_base_url", "recording_user", "recording_pass")
    base_url = settings.get("recording_base_url", "")
    user = settings.get("recording_user", "")
    password = settings.get("recording_pass", "")

    if not base_url or not user or not password:
        raise RuntimeError("Missing recording_base_url/recording_user/recording_pass in app_settings")

    base = base_url if base_url.endswith("/") else base_url + "/"
    return f"{base}live/{user}/{password}/{stream_id}.ts"


def load_app_settings(*keys):
    if not keys:
        return {}

    placeholders = ",".join("?" for _ in keys)
    with db_connect() as con:
        rows = con.execute(
            f"SELECT key, value FROM app_settings WHERE key IN ({placeholders})",
            keys,
        ).fetchall()

    return {r["key"]: r["value"] for r in rows}


def load_runtime_settings():
    global RECORDING_PADDING_SECONDS, CONVERSION_RECORDING_SAFETY_SECONDS, MIN_DURATION_RATIO, MIN_AVG_BITRATE_KBPS

    try:
        settings = load_app_settings(
            "recording_padding_seconds",
            "conversion_recording_safety_seconds",
            "min_duration_ratio",
            "min_avg_bitrate_kbps",
        )
    except Exception as e:
        log.warning(f"Runtime app_settings unavailable; using defaults: {e}")
        return

    RECORDING_PADDING_SECONDS = parse_int_setting(settings, "recording_padding_seconds", RECORDING_PADDING_SECONDS)
    CONVERSION_RECORDING_SAFETY_SECONDS = parse_int_setting(settings, "conversion_recording_safety_seconds", CONVERSION_RECORDING_SAFETY_SECONDS)
    MIN_DURATION_RATIO = parse_float_setting(settings, "min_duration_ratio", MIN_DURATION_RATIO)
    MIN_AVG_BITRATE_KBPS = parse_int_setting(settings, "min_avg_bitrate_kbps", MIN_AVG_BITRATE_KBPS)


def parse_int_setting(settings: dict, key: str, default: int) -> int:
    try:
        value = settings.get(key)
        if value in (None, ""):
            return default
        return int(value)
    except Exception:
        log.warning(f"Invalid integer app_setting {key}={settings.get(key)!r}; using {default}")
        return default


def parse_float_setting(settings: dict, key: str, default: float) -> float:
    try:
        value = settings.get(key)
        if value in (None, ""):
            return default
        return float(value)
    except Exception:
        log.warning(f"Invalid numeric app_setting {key}={settings.get(key)!r}; using {default}")
        return default


def log_recording_settings_status():
    try:
        settings = load_app_settings("recording_base_url", "recording_user", "recording_pass")
        base_ok = bool(settings.get("recording_base_url"))
        user_ok = bool(settings.get("recording_user"))
        pass_ok = bool(settings.get("recording_pass"))
        log.info(f"Recording Config: base_url={base_ok}, user={user_ok}, pass={pass_ok}")
    except Exception as e:
        log.warning(f"Recording Config: unavailable ({e})")


def update_recording_status(row_id: int, status: str, job_id: str = None, error: str = None):
    try:
        with db_connect() as con:
            ensure_recording_failure_tables(con)
            if error:
                log.info(f"DB STATUS -> {row_id} | {status} | {error}")
            else:
                log.info(f"DB STATUS -> {row_id} | {status}")

            con.execute(
                """
                UPDATE scheduled_recordings
                SET status = @status,
                    job_id = COALESCE(@job_id, job_id),
                    failure_reason = CASE
                        WHEN @error IS NOT NULL AND @error <> '' THEN @error
                        WHEN @status IN ('scheduled','queued','recording','completed','cancelled','skipped -> too short') THEN NULL
                        ELSE failure_reason
                    END
                WHERE id = @id
                """,
                {"status": status, "job_id": job_id, "error": error, "id": row_id},
            )
            if status == "failed" and is_provider_failure(error):
                record_channel_failure(con, row_id, job_id, error)
            con.commit()
            if error or status in ("failed", "timeout"):
                row = con.execute(
                    "SELECT channel, title FROM scheduled_recordings WHERE id = ?",
                    (row_id,),
                ).fetchone()
                log_system_event(
                    "error" if status in ("failed", "timeout") else "warn",
                    "recording",
                    f"Recording {status}",
                    job_id=job_id,
                    recording_id=row_id,
                    channel=row["channel"] if row else None,
                    title=row["title"] if row else None,
                    details=error,
                )
    except Exception as e:
        log.error(f"DB status update failed for row {row_id}: {e}")
        log_system_event(
            "error",
            "db",
            f"DB status update failed for row {row_id}",
            recording_id=row_id,
            job_id=job_id,
            details=str(e),
        )


def ensure_recording_failure_tables(con):
    try:
        con.execute("ALTER TABLE scheduled_recordings ADD COLUMN failure_reason TEXT")
    except sqlite3.OperationalError as e:
        if "duplicate column name" not in str(e).lower():
            raise

    con.execute(
        """
        CREATE TABLE IF NOT EXISTS channel_recording_failures (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            channel TEXT NOT NULL,
            job_id TEXT,
            title TEXT,
            start_time TEXT,
            failure_reason TEXT,
            failed_at TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            UNIQUE(job_id)
        )
        """
    )
    con.execute(
        """
        CREATE TABLE IF NOT EXISTS channel_health (
            channel TEXT PRIMARY KEY,
            failed_count_7_days INTEGER NOT NULL DEFAULT 0,
            last_failed_at TEXT,
            last_failure_reason TEXT,
            is_suspect INTEGER NOT NULL DEFAULT 0,
            suspect_until TEXT
        )
        """
    )


def is_provider_failure(error: str) -> bool:
    if not error:
        return False
    text = error.lower()
    return any(
        marker in text
        for marker in (
            "403",
            "404",
            "forbidden",
            "access denied",
            "error opening input",
            "connection",
            "timed out",
            "timeout",
            "output file missing",
            "too small",
        )
    )


def record_channel_failure(con, row_id: int, job_id: str, error: str):
    row = con.execute(
        """
        SELECT channel, title, start_time
        FROM scheduled_recordings
        WHERE id = ?
        """,
        (row_id,),
    ).fetchone()
    if not row or not row["channel"]:
        return

    con.execute(
        """
        INSERT OR IGNORE INTO channel_recording_failures
            (channel, job_id, title, start_time, failure_reason)
        VALUES
            (?, ?, ?, ?, ?)
        """,
        (row["channel"], job_id, row["title"], row["start_time"], error),
    )
    failed_count = con.execute(
        """
        SELECT COUNT(*)
        FROM channel_recording_failures
        WHERE channel = ?
          AND failed_at >= datetime('now','localtime','-7 days')
        """,
        (row["channel"],),
    ).fetchone()[0]
    is_suspect = 1 if failed_count >= 2 else 0

    con.execute(
        """
        INSERT INTO channel_health
            (channel, failed_count_7_days, last_failed_at, last_failure_reason, is_suspect, suspect_until)
        VALUES
            (?, ?, datetime('now','localtime'), ?, ?,
             CASE WHEN ? = 1 THEN datetime('now','localtime','+7 days') ELSE NULL END)
        ON CONFLICT(channel) DO UPDATE SET
            failed_count_7_days = excluded.failed_count_7_days,
            last_failed_at = excluded.last_failed_at,
            last_failure_reason = excluded.last_failure_reason,
            is_suspect = excluded.is_suspect,
            suspect_until = excluded.suspect_until
        """,
        (row["channel"], failed_count, error, is_suspect, is_suspect),
    )


def ensure_system_events_table(con):
    con.execute(
        """
        CREATE TABLE IF NOT EXISTS system_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            event_time TEXT NOT NULL DEFAULT (datetime('now','localtime')),
            source TEXT NOT NULL,
            level TEXT NOT NULL,
            category TEXT NOT NULL,
            job_id TEXT,
            recording_id INTEGER,
            channel TEXT,
            title TEXT,
            message TEXT NOT NULL,
            details TEXT
        )
        """
    )
    con.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_system_events_time
        ON system_events(event_time DESC)
        """
    )
    con.execute(
        """
        CREATE INDEX IF NOT EXISTS idx_system_events_category
        ON system_events(category, level, event_time DESC)
        """
    )


def log_system_event(
    level: str,
    category: str,
    message: str,
    *,
    job_id: str = None,
    recording_id: int = None,
    channel: str = None,
    title: str = None,
    details: str = None,
):
    try:
        with db_connect() as con:
            ensure_system_events_table(con)
            con.execute(
                """
                INSERT INTO system_events
                    (source, level, category, job_id, recording_id, channel, title, message, details)
                VALUES
                    ('mac_server', @level, @category, @job_id, @recording_id, @channel, @title, @message, @details)
                """,
                {
                    "level": level,
                    "category": category,
                    "job_id": job_id,
                    "recording_id": recording_id,
                    "channel": channel,
                    "title": title,
                    "message": message,
                    "details": details,
                },
            )
            con.commit()
    except Exception as e:
        log.warning(f"system event write skipped -> {e}")


def fetch_upcoming_rows(limit=50):
    with db_connect() as con:
        rows = con.execute(
            """
            SELECT sr.id, sr.title, sr.channel, sr.start_time, sr.end_time,
                   sr.program_type, sr.season_number, sr.episode_number,
                   sr.episode_title, sr.status, sr.job_id
            FROM scheduled_recordings sr
            WHERE sr.status IN ('scheduled', 'queued', 'recording')
              AND sr.end_time > datetime('now', 'localtime')
            ORDER BY sr.start_time
            LIMIT @limit
            """,
            {"limit": limit},
        ).fetchall()

    return [dict(r) for r in rows]


def claim_due_recordings():
    now = datetime.now()
    due = []

    with db_connect() as con:
        rows = con.execute(
            """
            SELECT sr.id, sr.title, sr.channel, sr.start_time, sr.end_time,
                   sr.program_type, sr.season_number, sr.episode_number,
                   sr.episode_title,
                   COALESCE(c.stream_id, c2.stream_id) AS stream_id
            FROM scheduled_recordings sr
            LEFT JOIN channels c
              ON c.channel_id = sr.channel
            LEFT JOIN channels c2
              ON c2.sd_station_id = sr.channel
             AND c2.stream_id IS NOT NULL
             AND c2.stream_id != 0
             AND COALESCE(c2.is_bad, 0) = 0
            WHERE sr.status = 'scheduled'
              AND sr.start_time <= datetime('now', 'localtime', '+' || @claim_window || ' seconds')
              AND sr.end_time > datetime('now', 'localtime')
              AND NOT EXISTS (
                  SELECT 1
                  FROM scheduled_recordings active
                  WHERE active.channel = sr.channel
                    AND active.status = 'recording'
                    AND active.id != sr.id
              )
            GROUP BY sr.id
            ORDER BY sr.start_time
            """,
            {"claim_window": CLAIM_WINDOW_SECONDS},
        ).fetchall()

        for row in rows:
            job_id = f"db_{row['id']}_{int(time.time())}"
            cur = con.execute(
                """
                UPDATE scheduled_recordings
                SET status = 'queued',
                    job_id = @job_id
                WHERE id = @id
                  AND status = 'scheduled'
                """,
                {"job_id": job_id, "id": row["id"]},
            )

            if cur.rowcount == 1:
                item = dict(row)
                item["job_id"] = job_id
                item["claimed_at"] = now
                due.append(item)

        con.commit()

    return due


# =========================================================
# ROUTES
# =========================================================

@app.route("/ping")
def ping():
    return jsonify({
        "status": "ok",
        "version": SERVER_VERSION,
        "time": datetime.now().isoformat(),
        "db_path": DB_PATH,
        "db_exists": Path(DB_PATH).exists(),
        "active_jobs": len([j for j in jobs.values() if j["status"] == "recording"])
    })


@app.route("/record", methods=["POST"])
def record():
    """
    POST /record
    Body: { job_id, title, url, duration, start_time,
            program_type, season_number, episode_number, episode_title,
            scheduled_row_id }
    """
    try:
        data = request.get_json()

        required = ["job_id", "title", "url", "duration"]
        for field in required:
            if field not in data:
                return jsonify({"error": f"Missing field: {field}"}), 400

        job_id = data["job_id"]
        title = data["title"]
        url = data["url"]
        duration = int(data["duration"])
        program_type = data.get("program_type", "")
        season_number = int(data.get("season_number", 0) or 0)
        episode_number = int(data.get("episode_number", 0) or 0)
        episode_title = data.get("episode_title", "")
        scheduled_row_id = data.get("scheduled_row_id")

        log.info(f"JOB RECEIVED -> {job_id} | {title} | {duration}s | {program_type} S{season_number}E{episode_number}")

        start_recording_thread(
            job_id=job_id,
            title=title,
            url=url,
            duration=duration,
            program_type=program_type,
            season_number=season_number,
            episode_number=episode_number,
            episode_title=episode_title,
            scheduled_row_id=scheduled_row_id,
        )

        return jsonify({"job_id": job_id, "status": "queued"})

    except Exception as e:
        log.error(f"POST /record error -> {e}")
        return jsonify({"error": str(e)}), 500


@app.route("/status/<job_id>")
def status(job_id):
    with jobs_lock:
        job = jobs.get(job_id)

    if not job:
        return jsonify({"error": "job not found"}), 404

    return jsonify({
        "job_id": job_id,
        "status": job["status"],
        "title": job["title"],
        "file": job["file"],
        "error": job["error"],
        "started": job["started"],
        "finished": job["finished"]
    })


@app.route("/jobs")
def list_jobs():
    with jobs_lock:
        return jsonify(jobs)


@app.route("/jobs/active")
def active_jobs():
    with jobs_lock:
        active = {
            jid: j for jid, j in jobs.items()
            if j["status"] in ("queued", "recording")
        }
    return jsonify(active)


@app.route("/convert_firetv", methods=["POST"])
def convert_firetv():
    """
    POST /convert_firetv
    Body: { title, queue }
    Converts a matching Fire TV .ts capture into a Plex movie .mp4.
    The .ts source is deleted only after ffmpeg and ffprobe both succeed.
    """
    try:
        data = request.get_json() or {}
        title = (data.get("title") or "").strip()
        if not title:
            return jsonify({"error": "Missing field: title"}), 400

        if bool(data.get("queue")):
            item, added = enqueue_firetv_conversion(title)
            status_code = 202 if added else 200
            return jsonify({
                "status": "queued" if added else item.get("status", "queued"),
                "queued": added,
                "item": item,
                "queue": conversion_queue_snapshot(),
            }), status_code

        guard = conversion_start_guard(title)
        if guard:
            log_system_event(
                "warn",
                "conversion",
                guard.get("error", "Fire TV conversion deferred"),
                title=title,
                details=json.dumps(guard),
            )
            return jsonify(guard), 409

        if not conversion_lock.acquire(blocking=False):
            log_system_event(
                "warn",
                "conversion",
                "Fire TV conversion already running",
                title=title,
                details=f"active_title={active_conversion}",
            )
            return jsonify({"error": "conversion already running", "active_title": active_conversion}), 409

        set_active_conversion(title)
        try:
            result = run_firetv_conversion(title)
            return jsonify(result)
        finally:
            set_active_conversion(None)
            conversion_lock.release()
    except FileNotFoundError as e:
        return jsonify({"error": str(e), "title": title if "title" in locals() else None}), 404
    except Exception as e:
        log.error(f"POST /convert_firetv error -> {e}")
        log_system_event(
            "error",
            "conversion",
            "Fire TV conversion failed",
            title=title if "title" in locals() else None,
            details=str(e),
        )
        return jsonify({"error": str(e)}), 500


@app.route("/convert_queue")
def convert_queue_status():
    return jsonify(conversion_queue_snapshot())


@app.route("/convert_queue/clear", methods=["POST"])
def clear_convert_queue():
    removed = []
    with conversion_queue_lock:
        keep = []
        for item in conversion_queue:
            if item.get("status") == "running":
                keep.append(item)
            else:
                removed.append(dict(item))
        conversion_queue[:] = keep
        if removed:
            conversion_queue_history.extend(removed[-50:])
            del conversion_queue_history[:-50]

    log.info(f"CONVERT QUEUE CLEARED -> {len(removed)} waiting item(s) removed")
    log_system_event("warn", "conversion", "Fire TV conversion queue cleared", details=f"removed={len(removed)}")
    return jsonify({
        "status": "ok",
        "removed": len(removed),
        "queue": conversion_queue_snapshot(),
    })


@app.route("/cancel/<job_id>", methods=["POST"])
def cancel(job_id):
    with jobs_lock:
        job = jobs.get(job_id)

    if not job:
        return jsonify({"error": "job not found"}), 404

    pid = job.get("pid")
    if pid:
        try:
            subprocess.run(["kill", str(pid)])
            with jobs_lock:
                jobs[job_id]["status"] = "cancelled"
            if job.get("scheduled_row_id"):
                update_recording_status(job["scheduled_row_id"], "cancelled", job_id)
            log.info(f"CANCELLED -> {job_id} | PID {pid}")
            return jsonify({"job_id": job_id, "status": "cancelled"})
        except Exception as e:
            return jsonify({"error": str(e)}), 500

    return jsonify({"error": "no active process"}), 400


@app.route("/schedule", methods=["POST"])
def update_schedule():
    """POST /schedule - compatibility endpoint. DB is now source of truth."""
    try:
        data = request.get_json()
        with open(UPCOMING_JSON_PATH, "w") as f:
            json.dump(data, f, indent=2)
        log.info(f"Schedule JSON updated -> {len(data)} recordings")
        return jsonify({"status": "ok", "count": len(data), "source": "json_compat"})
    except Exception as e:
        log.error(f"POST /schedule error -> {e}")
        return jsonify({"error": str(e)}), 500


@app.route("/upcoming")
def upcoming():
    """GET /upcoming - upcoming recordings from the shared SQLite DB."""
    try:
        return jsonify(fetch_upcoming_rows())
    except Exception as e:
        log.error(f"GET /upcoming error -> {e}")
        return jsonify({"error": str(e)}), 500


@app.route("/upcoming/html")
def upcoming_html():
    """GET /upcoming/html - human readable DB schedule."""
    try:
        data = fetch_upcoming_rows(100)

        rows = ""
        for r in data:
            ep = ""
            if r.get("season_number") and r.get("episode_number"):
                ep = f" [S{int(r['season_number']):02d}E{int(r['episode_number']):02d}]"
                if r.get("episode_title"):
                    ep += f" - {r['episode_title']}"

            rows += f"""
            <tr>
                <td>{r['title']}{ep}</td>
                <td>{r['channel']}</td>
                <td>{r['start_time']}</td>
                <td>{r['end_time']}</td>
                <td><span class="status {r['status']}">{r['status']}</span></td>
            </tr>"""

        html = f"""<!DOCTYPE html>
<html>
<head>
    <title>EPG Upcoming Recordings</title>
    <meta http-equiv="refresh" content="60">
    <style>
        body {{ font-family: Arial, sans-serif; padding: 20px; background: #1a1a2e; color: #eee; }}
        h2 {{ color: #e94560; }}
        table {{ width: 100%; border-collapse: collapse; }}
        th {{ background: #16213e; padding: 10px; text-align: left; color: #e94560; }}
        td {{ padding: 8px 10px; border-bottom: 1px solid #333; }}
        tr:hover {{ background: #16213e; }}
        .scheduled {{ color: #4fc3f7; }}
        .recording {{ color: #81c784; font-weight: bold; }}
        .queued {{ color: #ffb74d; }}
        .completed {{ color: #aaa; }}
        .failed {{ color: #ef5350; }}
    </style>
</head>
<body>
    <h2>EPG Upcoming Recordings</h2>
    <p style="color:#888">Auto-refreshes every 60 seconds | {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}</p>
    <table>
        <tr>
            <th>Title</th>
            <th>Channel</th>
            <th>Start</th>
            <th>End</th>
            <th>Status</th>
        </tr>
        {rows}
    </table>
</body>
</html>"""

        return html

    except Exception as e:
        log.error(f"GET /upcoming/html error -> {e}")
        return f"<h2>Error: {e}</h2>", 500


# =========================================================
# SCHEDULER WORKER
# =========================================================

def scheduler_loop():
    log.info("DB scheduler loop started")
    last_status_log = 0

    while not stop_scheduler.is_set():
        try:
            if not Path(DB_PATH).exists():
                log.warning(f"DB not found: {DB_PATH}")
                if not getattr(scheduler_loop, "_db_missing_logged", False):
                    log_system_event("warn", "db", "Movies DB path not found", details=DB_PATH)
                    scheduler_loop._db_missing_logged = True
            else:
                scheduler_loop._db_missing_logged = False
                due = claim_due_recordings()
                for item in due:
                    start_due_recording(item)
        except sqlite3.OperationalError as e:
            if is_sqlite_locked_error(e):
                log.warning("scheduler DB busy; Windows import likely has the lock, will retry")
            else:
                log.error(f"scheduler loop error -> {e}")
                log_system_event("error", "scheduler", "Scheduler database error", details=str(e))
        except Exception as e:
            log.error(f"scheduler loop error -> {e}")
            log_system_event("error", "scheduler", "Scheduler loop error", details=str(e))

        now = time.time()
        if now - last_status_log >= STATUS_INTERVAL_SECONDS:
            log_runtime_status()
            last_status_log = now

        stop_scheduler.wait(SCHEDULER_INTERVAL_SECONDS)


def set_active_conversion(title):
    global active_conversion
    with jobs_lock:
        active_conversion = title


def log_runtime_status():
    with jobs_lock:
        recording_titles = [
            j.get("title", "")
            for j in jobs.values()
            if j.get("status") == "recording"
        ]
        conversion_title = active_conversion

    with conversion_queue_lock:
        queued_count = sum(1 for item in conversion_queue if item.get("status") in ("queued", "deferred"))

    parts = []
    if recording_titles:
        parts.append("recording: " + ", ".join(recording_titles[:4]))
        if len(recording_titles) > 4:
            parts.append(f"+{len(recording_titles) - 4} more recording")
    else:
        parts.append("recording: none")

    if conversion_title:
        parts.append(f"converting: {conversion_title}")
    else:
        parts.append("converting: none")

    parts.append(f"conversion queue: {queued_count}")
    log.info("STATUS -> " + " | ".join(parts))


def conversion_queue_snapshot():
    with conversion_queue_lock:
        queued = [dict(item) for item in conversion_queue]
        waiting_count = sum(1 for item in queued if item.get("status") in ("queued", "deferred"))
        history = [dict(item) for item in conversion_queue_history[-25:]]

    with jobs_lock:
        active = active_conversion

    return {
        "active": active,
        "queued_count": waiting_count,
        "queued": queued,
        "recent": history,
    }


def enqueue_firetv_conversion(title: str):
    now = datetime.now().isoformat()
    norm = normalize_title(title)

    with conversion_queue_lock:
        for item in conversion_queue:
            if normalize_title(item.get("title", "")) == norm and item.get("status") in ("queued", "deferred"):
                return item, False

        item = {
            "id": f"conv_{int(time.time())}_{len(conversion_queue) + 1}",
            "title": title,
            "status": "queued",
            "queued_at": now,
            "started_at": None,
            "finished_at": None,
            "message": None,
        }
        conversion_queue.append(item)

    log.info(f"CONVERT QUEUED -> {title}")
    log_system_event("info", "conversion", "Fire TV conversion queued", title=title)
    return item, True


def update_conversion_queue_item(item, status: str, message: str = None):
    with conversion_queue_lock:
        item["status"] = status
        item["message"] = message
        if status == "running":
            item["started_at"] = datetime.now().isoformat()
        if status in ("done", "failed"):
            item["finished_at"] = datetime.now().isoformat()


def finish_conversion_queue_item(item):
    with conversion_queue_lock:
        if item in conversion_queue:
            conversion_queue.remove(item)
        conversion_queue_history.append(dict(item))
        del conversion_queue_history[:-50]


def conversion_queue_loop():
    log.info("Fire TV conversion queue loop started")

    while not stop_scheduler.is_set():
        item = None
        with conversion_queue_lock:
            if conversion_queue:
                item = conversion_queue[0]

        if not item:
            stop_scheduler.wait(10)
            continue

        title = item["title"]
        guard = conversion_start_guard(title)
        if guard:
            update_conversion_queue_item(item, "deferred", guard.get("error", "conversion deferred"))
            stop_scheduler.wait(60)
            continue

        if not conversion_lock.acquire(blocking=False):
            update_conversion_queue_item(item, "deferred", f"conversion already running: {active_conversion}")
            stop_scheduler.wait(30)
            continue

        set_active_conversion(title)
        update_conversion_queue_item(item, "running")
        try:
            result = run_firetv_conversion(title)
            update_conversion_queue_item(item, "done", result.get("file"))
        except Exception as e:
            log.error(f"CONVERT QUEUE FAILED -> {title} | {e}")
            log_system_event("error", "conversion", "Queued Fire TV conversion failed", title=title, details=str(e))
            update_conversion_queue_item(item, "failed", str(e))
        finally:
            set_active_conversion(None)
            conversion_lock.release()
            finish_conversion_queue_item(item)

        stop_scheduler.wait(3)


def active_recording_titles():
    with jobs_lock:
        return [
            j["title"]
            for j in jobs.values()
            if j.get("status") in ("queued", "recording")
        ]


def next_scheduled_recording_within(seconds: int):
    with db_connect() as con:
        return con.execute(
            """
            SELECT title, start_time
            FROM scheduled_recordings
            WHERE status = 'scheduled'
              AND start_time >= datetime('now', 'localtime')
              AND start_time <= datetime('now', 'localtime', '+' || @seconds || ' seconds')
            ORDER BY start_time
            LIMIT 1
            """,
            {"seconds": seconds},
        ).fetchone()


def conversion_start_guard(title: str):
    active_titles = active_recording_titles()
    if active_titles:
        return None

    try:
        next_row = next_scheduled_recording_within(CONVERSION_RECORDING_SAFETY_SECONDS)
        if next_row:
            return {
                "error": "recording starts soon; conversion deferred",
                "title": title,
                "next_recording": next_row["title"],
                "start_time": next_row["start_time"],
            }
    except sqlite3.OperationalError as e:
        if is_sqlite_locked_error(e):
            return {"error": "DB busy; conversion deferred", "title": title}
        raise

    return None


def start_due_recording(item):
    row_id = item["id"]
    title = item["title"]
    stream_id = item["stream_id"]

    if not stream_id:
        update_recording_status(row_id, "failed", item["job_id"], "missing stream_id")
        return

    try:
        start_time = parse_db_time(item["start_time"])
        end_time = parse_db_time(item["end_time"])
        now = datetime.now()
        padding = trailing_padding_for_recording(row_id, item["channel"], end_time)
        duration = int(max((end_time - max(now, start_time)).total_seconds(), 0)) + padding

        if duration < 60:
            update_recording_status(row_id, "skipped -> too short", item["job_id"])
            return

        url = build_stream_url(str(stream_id))
        program_type = item["program_type"] or ""
        season_number = int(item["season_number"] or 0)
        episode_number = int(item["episode_number"] or 0)
        episode_title = item["episode_title"] or ""

        start_recording_thread(
            job_id=item["job_id"],
            title=title,
            url=url,
            duration=duration,
            program_type=program_type,
            season_number=season_number,
            episode_number=episode_number,
            episode_title=episode_title,
            scheduled_row_id=row_id,
        )
    except Exception as e:
        update_recording_status(row_id, "failed", item["job_id"], str(e))


def start_recording_thread(job_id: str, title: str, url: str, duration: int,
                           program_type: str = "", season_number: int = 0,
                           episode_number: int = 0, episode_title: str = "",
                           scheduled_row_id=None):
    with jobs_lock:
        jobs[job_id] = {
            "status": "queued",
            "title": title,
            "url": url,
            "duration": duration,
            "program_type": program_type,
            "season_number": season_number,
            "episode_number": episode_number,
            "episode_title": episode_title,
            "scheduled_row_id": scheduled_row_id,
            "pid": None,
            "file": None,
            "error": None,
            "started": datetime.now().isoformat(),
            "finished": None
        }

    if scheduled_row_id:
        update_recording_status(scheduled_row_id, "queued", job_id)

    t = threading.Thread(
        target=run_ffmpeg,
        args=(job_id, title, url, duration, program_type, season_number, episode_number, episode_title, scheduled_row_id),
        daemon=True
    )
    t.start()


def trailing_padding_for_recording(row_id: int, channel: str, end_time: datetime) -> int:
    """Avoid overlapping the same stream when episodes air back-to-back."""
    try:
        with db_connect() as con:
            next_row = con.execute(
                """
                SELECT id, start_time
                FROM scheduled_recordings
                WHERE id != @id
                  AND channel = @channel
                  AND status IN ('scheduled', 'queued', 'recording')
                  AND start_time >= @end_time
                  AND start_time <= datetime(@end_time, '+' || @padding || ' seconds')
                ORDER BY start_time
                LIMIT 1
                """,
                {
                    "id": row_id,
                    "channel": channel,
                    "end_time": end_time.strftime("%Y-%m-%d %H:%M:%S"),
                    "padding": RECORDING_PADDING_SECONDS,
                },
            ).fetchone()

        if next_row:
            log.info(f"PADDING SKIPPED -> row {row_id}; next same-channel row {next_row['id']} starts at {next_row['start_time']}")
            return 0
    except Exception as e:
        log.warning(f"padding check failed for row {row_id}: {e}")

    return RECORDING_PADDING_SECONDS


# =========================================================
# FFMPEG WORKER
# =========================================================

def run_ffmpeg(job_id: str, title: str, url: str, duration: int,
               program_type: str = "", season_number: int = 0,
               episode_number: int = 0, episode_title: str = "",
               scheduled_row_id=None):
    try:
        with jobs_lock:
            jobs[job_id]["status"] = "recording"

        if scheduled_row_id:
            update_recording_status(scheduled_row_id, "recording", job_id)

        is_series = (program_type == "EP" and season_number > 0 and episode_number > 0)

        if is_series:
            series_title = clean_series_title(title)
            ep_code = f"S{season_number:02d}E{episode_number:02d}"
            ep_suffix = f" - {episode_title}" if episode_title else ""
            safe_name = clean_filename(f"{series_title} - {ep_code}{ep_suffix}")
            season_dir = f"Season {season_number:02d}"
            folder = Path(NAS_TV_PATH) / clean_filename(series_title) / season_dir
        else:
            safe_name = clean_filename(title)
            folder = Path(NAS_MOVIES_PATH) / safe_name

        folder.mkdir(parents=True, exist_ok=True)
        output_path = folder / f"{safe_name}.mp4"
        partial_path = folder / f"{safe_name}.part.mp4"
        ffmpeg_log_path = folder / f"{safe_name}.ffmpeg.log"

        log.info(f"FFMPEG START -> {job_id} | {title}")
        log.info(f"OUTPUT -> {output_path}")

        if partial_path.exists():
            partial_path.unlink()

        cmd = [
            FFMPEG_PATH,
            "-y",
            "-loglevel", "error",
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_at_eof", "1",
            "-reconnect_delay_max", "30",
            "-rw_timeout", "15000000",
            "-i", url,
            "-t", str(duration),
            "-c", "copy",
            str(partial_path)
        ]

        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

        with jobs_lock:
            jobs[job_id]["pid"] = proc.pid
            jobs[job_id]["file"] = str(partial_path)

        stdout, stderr = proc.communicate()
        err = stderr.decode(errors="replace").strip()
        if err:
            ffmpeg_log_path.write_text(err)

        if proc.returncode == 0:
            if partial_path.exists() and partial_path.stat().st_size > 1_000_000:
                actual_duration = probe_duration(partial_path)
                min_duration = duration * MIN_DURATION_RATIO

                if actual_duration is None:
                    raise Exception("recording duration could not be verified")

                if actual_duration is not None and actual_duration < min_duration:
                    raise Exception(f"recording too short: {actual_duration:.0f}s of expected {duration}s")

                avg_bitrate_kbps = (partial_path.stat().st_size * 8) / max(actual_duration, 1) / 1000
                if avg_bitrate_kbps < MIN_AVG_BITRATE_KBPS:
                    raise Exception(f"recording bitrate too low: {avg_bitrate_kbps:.0f} kbps")

                if output_path.exists():
                    output_path.unlink()
                partial_path.rename(output_path)
                size_gb = output_path.stat().st_size / 1e9
                duration_msg = f"{actual_duration:.0f}s" if actual_duration is not None else "unknown duration"
                log.info(f"COMPLETED -> {job_id} | {title} | {size_gb:.2f}GB | {duration_msg} | {avg_bitrate_kbps:.0f} kbps")
                with jobs_lock:
                    jobs[job_id]["status"] = "done"
                    jobs[job_id]["file"] = str(output_path)
                    jobs[job_id]["finished"] = datetime.now().isoformat()
                if scheduled_row_id:
                    update_recording_status(scheduled_row_id, "completed", job_id)
            else:
                raise Exception("Output file missing or too small")
        else:
            raise Exception(f"ffmpeg error: {err}")

    except Exception as e:
        log.error(f"FAILED -> {job_id} | {e}")
        with jobs_lock:
            if job_id in jobs:
                jobs[job_id]["status"] = "failed"
                jobs[job_id]["error"] = str(e)
                jobs[job_id]["finished"] = datetime.now().isoformat()
        if scheduled_row_id:
            update_recording_status(scheduled_row_id, "failed", job_id, str(e))


# =========================================================
# HELPERS
# =========================================================

def clean_filename(name: str) -> str:
    import re
    years = re.findall(r"\(\d{4}\)", name)
    if len(years) > 1:
        name = re.sub(r"\s*\(\d{4}\)", "", name).strip()
        name = f"{name} {years[0]}"
    invalid = r'\/:*?"<>|'
    for ch in invalid:
        name = name.replace(ch, "")
    return name.strip()


def clean_series_title(name: str) -> str:
    import re
    return re.sub(r"\s*\(\d{4}\)\s*$", "", name).strip()


def normalize_title(name: str) -> str:
    import re
    path = Path(name)
    stem = path.stem if path.suffix.lower() in (".ts", ".mp4", ".mkv", ".avi", ".mov", ".m4v") else name
    stem = re.sub(r"[_\s-]*\d{8}[_-]\d{6}$", "", stem)
    stem = re.sub(r"\s*\(\d{4}\)\s*$", "", stem)
    stem = re.sub(r"[_\s-]+(?:19|20)\d{2}$", "", stem)
    stem = re.sub(r"\s+", " ", stem)
    return re.sub(r"[^a-z0-9]+", "", stem.lower())


def find_fire_tv_capture(title: str):
    target_norm = normalize_title(title)
    candidates = []
    missing_paths = []
    for fire_tv_path in FIRE_TV_PATHS:
        fire_dir = Path(fire_tv_path)
        if not fire_dir.exists():
            missing_paths.append(str(fire_dir))
            continue
        for path in fire_dir.rglob("*.ts"):
            if normalize_title(path.name) == target_norm:
                candidates.append(path)

    if not candidates:
        if missing_paths and len(missing_paths) == len(FIRE_TV_PATHS):
            raise RuntimeError(f"Fire TV paths not found on Mac: {', '.join(missing_paths)}")
        return None

    candidates.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return candidates[0]


def find_matching_fire_tv_captures(title: str):
    target_norm = normalize_title(title)
    candidates = []
    for fire_tv_path in FIRE_TV_PATHS:
        fire_dir = Path(fire_tv_path)
        if not fire_dir.exists():
            continue
        for path in fire_dir.rglob("*.ts"):
            if normalize_title(path.name) == target_norm:
                candidates.append(path)
    return list(dict.fromkeys(candidates))


def convert_capture_to_plex_movie(title: str, source_path: Path):
    safe_name = clean_filename(title)
    folder = Path(NAS_MOVIES_PATH) / safe_name
    folder.mkdir(parents=True, exist_ok=True)

    output_path = folder / f"{safe_name}.mp4"
    partial_path = folder / f"{safe_name}.part.mp4"
    ffmpeg_log_path = folder / f"{safe_name}.convert.ffmpeg.log"

    if partial_path.exists():
        partial_path.unlink()

    source_duration = probe_duration(source_path)
    if source_duration is None or source_duration < 600:
        raise RuntimeError(f"source duration could not be verified: {source_duration}")

    log.info(f"CONVERT START -> {title}")
    log.info(f"SOURCE -> {source_path}")
    log.info(f"OUTPUT -> {output_path}")

    cmd = [
        FFMPEG_PATH,
        "-y",
        "-loglevel", "error",
        "-i", str(source_path),
        "-c", "copy",
        str(partial_path),
    ]

    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    err = (proc.stderr or "").strip()
    if err:
        ffmpeg_log_path.write_text(err)

    if proc.returncode != 0:
        raise RuntimeError(f"ffmpeg convert failed: {err}")

    if not partial_path.exists() or partial_path.stat().st_size < 1_000_000:
        raise RuntimeError("converted file missing or too small")

    duration = probe_duration(partial_path)
    if duration is None or duration < 60:
        raise RuntimeError("converted file duration could not be verified")
    if duration < source_duration * MIN_DURATION_RATIO:
        raise RuntimeError(f"converted file too short: {duration:.0f}s of source {source_duration:.0f}s")

    if output_path.exists():
        output_path.unlink()
    partial_path.rename(output_path)

    deleted_sources = []
    for ts_path in find_matching_fire_tv_captures(title):
        try:
            ts_path.unlink()
            deleted_sources.append(str(ts_path))
        except FileNotFoundError:
            continue
        except Exception as e:
            log.warning(f"CONVERT CLEANUP SKIPPED -> {ts_path} | {e}")

    log.info(f"CONVERT DONE -> {title} | {output_path} | {duration:.0f}s of {source_duration:.0f}s | sources deleted: {len(deleted_sources)}")
    return {
        "status": "done",
        "title": title,
        "source_deleted": str(source_path),
        "deleted_sources": deleted_sources,
        "file": str(output_path),
        "duration": duration,
        "source_duration": source_duration,
    }


def run_firetv_conversion(title: str):
    source_path = find_fire_tv_capture(title)
    if not source_path:
        log.warning(f"CONVERT FIRETV MISS -> {title} | paths={FIRE_TV_PATHS}")
        log_system_event(
            "warn",
            "conversion",
            "Fire TV source file not found",
            title=title,
            details=f"paths={FIRE_TV_PATHS}",
        )
        raise FileNotFoundError(f"No matching .ts file found in {', '.join(FIRE_TV_PATHS)}")

    log.info(f"CONVERT FIRETV START -> {title} | {source_path}")
    result = convert_capture_to_plex_movie(title, source_path)
    log.info(f"CONVERT FIRETV DONE -> {title} | {result.get('file')}")
    log_system_event(
        "info",
        "conversion",
        "Fire TV conversion completed",
        title=title,
        details=result.get("file"),
    )
    return result


def probe_duration(path: Path):
    ffprobe_path = str(Path(FFMPEG_PATH).with_name("ffprobe"))
    try:
        proc = subprocess.run(
            [
                ffprobe_path,
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                str(path),
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=30,
        )
        if proc.returncode != 0:
            log.warning(f"ffprobe failed for {path}: {proc.stderr.strip()}")
            return None
        return float(proc.stdout.strip())
    except Exception as e:
        log.warning(f"ffprobe error for {path}: {e}")
        return None


# =========================================================
# STARTUP
# =========================================================

if __name__ == "__main__":
    load_config()
    load_runtime_settings()

    log.info("=" * 50)
    log.info("EPG Mac Recording Server starting...")
    log.info(f"Version         : {SERVER_VERSION}")
    log.info(f"Config Path     : {CONFIG_PATH}")
    log.info(f"Config Loaded   : {config_loaded}")
    log.info(f"NAS Movies Path : {NAS_MOVIES_PATH}")
    log.info(f"NAS TV Path     : {NAS_TV_PATH}")
    log.info(f"Fire TV Paths   : {', '.join(FIRE_TV_PATHS)}")
    log.info(f"ffmpeg          : {FFMPEG_PATH}")
    log.info(f"Port            : {SERVER_PORT}")
    log.info(f"DB Path         : {DB_PATH}")
    log.info(f"DB Exists       : {Path(DB_PATH).exists()}")
    log.info(f"Record Padding  : {RECORDING_PADDING_SECONDS}s")
    log.info(f"Convert Safety  : {CONVERSION_RECORDING_SAFETY_SECONDS}s")
    log.info(f"Min Duration    : {MIN_DURATION_RATIO:.2f}")
    log.info(f"Min Bitrate     : {MIN_AVG_BITRATE_KBPS} kbps")
    log.info(f"Upcoming JSON   : {UPCOMING_JSON_PATH}")
    log_recording_settings_status()
    log.info("=" * 50)

    if not Path(FFMPEG_PATH).exists():
        log.error(f"ffmpeg not found at {FFMPEG_PATH}")
        log_system_event("error", "startup", "ffmpeg not found", details=FFMPEG_PATH)
        exit(1)

    if not Path(DB_PATH).exists():
        log_system_event("warn", "startup", "Movies DB path not found at startup", details=DB_PATH)

    scheduler_thread = threading.Thread(target=scheduler_loop, daemon=True)
    scheduler_thread.start()

    conversion_thread = threading.Thread(target=conversion_queue_loop, daemon=True)
    conversion_thread.start()

    app.run(
        host="0.0.0.0",
        port=SERVER_PORT,
        debug=False,
        threaded=True
    )
