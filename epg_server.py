"""
EPG Guide Import Server
Runs on QNAP. Replaces epgmanager.exe --import-guide-only.
DB is local — no network share locking issues.

Run: python3 epg_server.py
"""

import hashlib
import json
import logging
import os
import re
import shutil
import sqlite3
import threading
import time
import unicodedata
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from pathlib import Path

import requests
from flask import Flask, jsonify, request

# ── Config ────────────────────────────────────────────────────────────────────

CONFIG_PATH = os.environ.get("EPG_CONFIG", "/share/EPG/config.json")
PORT = int(os.environ.get("EPG_PORT", 5000))
VERSION = "1.0.0"
LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "epg_server.log")

SD_BASE_URL = "https://json.schedulesdirect.org/20141201"
DEFAULT_SD_LINEUP = "USA-DITV515-X"
BACKUP_RETENTION_DAYS = 7

# ── Logging ───────────────────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.FileHandler(LOG_PATH),
        logging.StreamHandler(),
    ],
)
log = logging.getLogger("epg")

# ── Flask app ─────────────────────────────────────────────────────────────────

app = Flask(__name__)

# ── Import state (mirrors server.py /guide_import/status pattern) ─────────────

_import_lock = threading.Lock()
_import_status = {
    "status": "idle",
    "started_at": None,
    "finished_at": None,
    "last_error": None,
    "last_output": [],
    "pid": None,
    "requested": False,
    "source": None,
    "force": False,
}

# ── Config loader ─────────────────────────────────────────────────────────────

def load_config():
    with open(CONFIG_PATH) as f:
        return json.load(f)


# ── Title helpers (TitleHelpers.vb) ───────────────────────────────────────────

def remove_diacritics(s: str) -> str:
    normalized = unicodedata.normalize("NFD", s)
    return "".join(c for c in normalized if unicodedata.category(c) != "Mn")


def normalize_title(title: str) -> str:
    if not title:
        return ""
    t = remove_diacritics(title)
    t = re.sub(r"[^\w\s]", "", t)
    t = re.sub(r"\[(HD|SD|FHD|UHD|4K)\]", "", t, flags=re.IGNORECASE)
    t = re.sub(r"\b(HD|SD|FHD|UHD|4K|1080P|720P|HDR)\b", "", t, flags=re.IGNORECASE)
    t = re.sub(r"^(NEW|PREMIERE|LIVE)\s*[:\-]\s*", "", t, flags=re.IGNORECASE)
    t = re.sub(r"^(US|UK|CA|AU|NZ)\s+PREMIERE[:\-]\s*", "", t, flags=re.IGNORECASE)
    t = re.sub(r"^(US|UK|CA|AU|NZ)\s*[\|\-]\s*", "", t, flags=re.IGNORECASE)
    t = re.sub(r"\(\d{4}\)", "", t)
    t = re.sub(r"\b\d{4}\b", "", t)
    t = t.replace(".", " ").replace("_", " ").replace("-", " ")
    t = re.sub(r"\-\s*$", "", t)
    t = re.sub(r"\s+", " ", t)
    return t.strip()


# ── Database helpers ──────────────────────────────────────────────────────────

def get_conn(db_path: str) -> sqlite3.Connection:
    con = sqlite3.connect(db_path, timeout=30)
    con.row_factory = sqlite3.Row
    con.execute("PRAGMA journal_mode=WAL")
    con.execute("PRAGMA busy_timeout=30000")
    return con


def rebuild_guide_database(db_path: str, output: list):
    """Drop and recreate the guide table — mirrors RebuildGuideDatabase in VB."""
    msg = "Rebuilding guide database..."
    log.info(msg); output.append(msg)
    con = get_conn(db_path)
    try:
        con.execute("DROP INDEX IF EXISTS idx_guide_unique")
        con.execute("DELETE FROM guide")
        con.commit()
        msg = "Guide table cleared."
        log.info(msg); output.append(msg)
    finally:
        con.close()


def create_guide_indexes(db_path: str, output: list):
    msg = "Creating guide lookup indexes..."
    log.info(msg); output.append(msg)
    con = get_conn(db_path)
    try:
        con.execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS idx_guide_unique
            ON guide(channel, start_utc)
        """)
        con.commit()
        msg = "Guide indexes created."
        log.info(msg); output.append(msg)
    except Exception as e:
        msg = f"Index creation warning (non-fatal): {e}"
        log.warning(msg); output.append(msg)
    finally:
        con.close()


# ── Backup helpers ────────────────────────────────────────────────────────────

def backup_database(db_path: str, label: str, config: dict, output: list):
    backup_dir = Path(CONFIG_PATH).parent / "backups"
    backup_dir.mkdir(exist_ok=True)
    stem = Path(db_path).stem
    backup_path = backup_dir / f"{stem}_{label}_{datetime.now().strftime('%Y%m%d_%H%M%S')}.db"
    shutil.copy2(db_path, backup_path)
    msg = f"DB backup created: {backup_path}"
    log.info(msg); output.append(msg)

    # Prune old backups
    cutoff = datetime.now() - timedelta(days=BACKUP_RETENTION_DAYS)
    for f in backup_dir.glob("Movies_*.db"):
        if datetime.fromtimestamp(f.stat().st_mtime) < cutoff:
            f.unlink()
            log.info(f"Old backup deleted: {f}")


# ── Guide update detector ─────────────────────────────────────────────────────

def guide_needs_update(guide_dir: str, stamp_file: str) -> bool:
    if not os.path.exists(stamp_file):
        return True
    try:
        with open(stamp_file) as f:
            stamp_text = f.read().strip()
        stamp_dt = datetime.fromisoformat(stamp_text)
        return datetime.now() - stamp_dt > timedelta(hours=23)
    except Exception:
        return True


def guide_db_is_empty(db_path: str) -> bool:
    try:
        con = get_conn(db_path)
        row = con.execute("SELECT COUNT(*) FROM guide").fetchone()
        con.close()
        return row[0] == 0
    except Exception:
        return True


def save_update_stamp(guide_dir: str, stamp_file: str):
    with open(stamp_file, "w") as f:
        f.write(datetime.now().isoformat())


# ── XMLTV import (GuideImporter.vb) ──────────────────────────────────────────

def import_xml(xml_file: str, db_path: str, output: list):
    msg = f"Importing XML guide from {xml_file}..."
    log.info(msg); output.append(msg)

    inserted = 0
    skipped = 0
    con = get_conn(db_path)
    try:
        con.execute("PRAGMA synchronous=OFF")
        con.execute("PRAGMA journal_mode=MEMORY")
        con.execute("PRAGMA temp_store=MEMORY")

        sql = """
            INSERT OR IGNORE INTO guide
                (title, normalized_title, channel, start_utc, end_utc, xml_file)
            VALUES (?, ?, ?, ?, ?, ?)
        """

        rows = []
        context = ET.iterparse(xml_file, events=("start",))
        for event, elem in context:
            if elem.tag == "programme":
                channel = elem.get("channel")
                start_attr = elem.get("start")
                stop_attr = elem.get("stop")
                if not channel or not start_attr or not stop_attr:
                    skipped += 1
                    elem.clear()
                    continue
                start_utc = start_attr[:14]
                end_utc = stop_attr[:14]
                title_el = elem.find("title")
                title = title_el.text if title_el is not None and title_el.text else ""
                if not title.strip():
                    skipped += 1
                    elem.clear()
                    continue
                norm = normalize_title(title)
                rows.append((title, norm, channel, start_utc, end_utc,
                             Path(xml_file).name))
                inserted += 1
                elem.clear()

                if len(rows) >= 5000:
                    con.executemany(sql, rows)
                    con.commit()
                    rows = []

        if rows:
            con.executemany(sql, rows)
            con.commit()

        msg = f"XML import complete: {inserted} inserted, {skipped} skipped."
        log.info(msg); output.append(msg)
    finally:
        con.close()


# ── Schedules Direct (SchedulesDirect.vb) ─────────────────────────────────────

_sd_token = ""
_sd_token_expiry = datetime.min


def sd_hash_password(password: str) -> str:
    return hashlib.sha1(password.encode("utf-8")).hexdigest()


def sd_authenticate(sd_user: str, sd_pass: str, output: list) -> bool:
    global _sd_token, _sd_token_expiry
    if _sd_token and datetime.now() < _sd_token_expiry:
        return True
    try:
        payload = {"username": sd_user, "password": sd_hash_password(sd_pass)}
        r = requests.post(
            f"{SD_BASE_URL}/token",
            json=payload,
            headers={"User-Agent": "EPGManager/1.0"},
            timeout=30,
        )
        data = r.json()
        if data.get("code") == 0:
            _sd_token = data["token"]
            _sd_token_expiry = datetime.now() + timedelta(hours=23)
            msg = "SD → Authenticated."
            log.info(msg); output.append(msg)
            return True
        else:
            msg = f"SD → Auth failed: {data}"
            log.error(msg); output.append(msg)
            return False
    except Exception as e:
        msg = f"SD → Auth error: {e}"
        log.error(msg); output.append(msg)
        return False


def sd_get_lineup_channels(lineups: list, output: list):
    channels = {}   # stationId -> channel number
    station_names = {}  # stationId -> name
    headers = {"User-Agent": "EPGManager/1.0", "token": _sd_token}
    for lineup in lineups:
        try:
            r = requests.get(
                f"{SD_BASE_URL}/lineups/{lineup}", headers=headers, timeout=30
            )
            data = r.json()
            if "response" in data:
                msg = f"SD → Lineup {lineup} error: {data['response']}"
                log.warning(msg); output.append(msg)
                continue
            for station in data.get("stations", []):
                sid = station["stationID"]
                station_names[sid] = station.get("name", sid)
            for mapping in data.get("map", []):
                sid = mapping["stationID"]
                if sid not in channels:
                    channels[sid] = mapping.get("channel", "")
            msg = f"SD → Lineup {lineup}: {len(channels)} channels so far."
            log.info(msg); output.append(msg)
        except Exception as e:
            msg = f"SD → GetLineupChannels error for {lineup}: {e}"
            log.error(msg); output.append(msg)
    return channels, station_names


def sd_sync_stations_to_channels(db_path: str, channels: dict, station_names: dict, output: list):
    con = get_conn(db_path)
    try:
        with con:
            for station_id in channels:
                channel_id = f"sd.{station_id}"
                name = station_names.get(station_id, channel_id)
                con.execute("""
                    INSERT OR IGNORE INTO channels
                        (channel_id, nickname, guide_channel, type, favorite, is_movie_channel)
                    VALUES (?, ?, ?, 'sd', 0, 0)
                """, (channel_id, name, channel_id))
        msg = f"SD → {len(channels)} stations synced to channels table."
        log.info(msg); output.append(msg)
    finally:
        con.close()


def sd_get_incremental_cutoff(db_path: str) -> datetime | None:
    try:
        con = get_conn(db_path)
        row = con.execute(
            "SELECT MAX(start_utc) FROM guide WHERE xml_file='schedules_direct'"
        ).fetchone()
        con.close()
        if row and row[0]:
            latest = datetime.fromisoformat(row[0])
            return latest - timedelta(hours=12)
    except Exception as e:
        log.warning(f"SD incremental cutoff error: {e}")
    return None


def sd_filter_existing_schedules(db_path: str, schedules: list) -> list:
    try:
        con = get_conn(db_path)
        existing = set()
        for row in con.execute("SELECT channel, start_utc FROM guide"):
            existing.add(f"{row[0]}\t{row[1]}")
        con.close()
        missing = [
            s for s in schedules
            if f"sd.{s['station_id']}\t{s['air_datetime'].strftime('%Y-%m-%d %H:%M:%S')}"
            not in existing
        ]
        return missing
    except Exception as e:
        log.warning(f"SD filter existing error: {e}")
        return schedules


def sd_get_schedules(station_ids: list, output: list) -> list:
    result = []
    date_list = [
        (datetime.now(timezone.utc) + timedelta(days=d)).strftime("%Y-%m-%d")
        for d in range(14)
    ]
    headers = {
        "User-Agent": "EPGManager/1.0",
        "token": _sd_token,
        "Content-Type": "application/json",
    }
    batch_size = 5000
    batches = [station_ids[i:i+batch_size] for i in range(0, len(station_ids), batch_size)]
    msg = f"SD → {len(batches)} schedule batch(es) for {len(station_ids)} stations."
    log.info(msg); output.append(msg)

    for i, batch in enumerate(batches):
        try:
            payload = [
                {"stationID": sid, "date": date_list}
                for sid in batch
            ]
            r = requests.post(
                f"{SD_BASE_URL}/schedules",
                json=payload,
                headers=headers,
                timeout=300,
            )
            data = r.json()
            for station in data:
                station_id = station.get("stationID", "")
                for program in station.get("programs", []):
                    try:
                        air_dt = datetime.fromisoformat(
                            program["airDateTime"].replace("Z", "+00:00")
                        ).astimezone().replace(tzinfo=None)
                        result.append({
                            "station_id": station_id,
                            "program_id": program["programID"],
                            "air_datetime": air_dt,
                            "duration": program["duration"],
                            "is_live": program.get("isLive", False),
                        })
                    except Exception:
                        pass
            msg = f"SD → Schedule batch {i+1}/{len(batches)} done ({len(result)} entries so far)."
            log.info(msg); output.append(msg)
        except Exception as e:
            msg = f"SD → GetSchedules batch {i+1} error: {e}"
            log.error(msg); output.append(msg)
    return result


def sd_get_programs(program_ids: list, output: list) -> dict:
    result = {}
    headers = {
        "User-Agent": "EPGManager/1.0",
        "token": _sd_token,
        "Content-Type": "application/json",
    }
    batch_size = 5000
    batches = [program_ids[i:i+batch_size] for i in range(0, len(program_ids), batch_size)]
    msg = f"SD → {len(batches)} program batch(es)."
    log.info(msg); output.append(msg)

    for i, batch in enumerate(batches):
        try:
            r = requests.post(
                f"{SD_BASE_URL}/programs",
                json=batch,
                headers=headers,
                timeout=300,
            )
            data = r.json()
            for program in data:
                try:
                    p = {}
                    p["program_id"] = program.get("programID", "")
                    p["program_type"] = p["program_id"][:2] if len(p["program_id"]) >= 2 else ""
                    p["is_movie"] = p["program_type"] == "MV"

                    # Title
                    title = ""
                    for t in program.get("titles", []):
                        for key in ("title120", "title60", "title40", "title10"):
                            if key in t:
                                title = t[key]
                                break
                        if title:
                            break
                    p["title"] = title
                    p["episode_title"] = program.get("episodeTitle150", "")

                    # Description
                    desc = ""
                    for d in program.get("descriptions", {}).get("description1000", []):
                        if d.get("descriptionLanguage") == "en":
                            desc = d.get("description", "")
                            break
                    if not desc:
                        for d in program.get("descriptions", {}).get("description1000", []):
                            desc = d.get("description", "")
                            break
                    p["description"] = desc

                    # Movie year
                    p["movie_year"] = str(program.get("movie", {}).get("year", "")) or ""

                    # Genres
                    p["genres"] = ",".join(program.get("genres", []))

                    # Season / episode
                    p["season_number"] = 0
                    p["episode_number"] = 0
                    for meta in program.get("metadata", []):
                        gn = meta.get("Gracenote", {})
                        p["season_number"] = gn.get("season", 0)
                        p["episode_number"] = gn.get("episode", 0)
                        break

                    if p["program_id"] not in result:
                        result[p["program_id"]] = p
                except Exception as e:
                    log.debug(f"SD program parse error: {e}")
            msg = f"SD → Program batch {i+1}/{len(batches)} done ({len(result)} programs so far)."
            log.info(msg); output.append(msg)
        except Exception as e:
            msg = f"SD → GetPrograms batch {i+1} error: {e}"
            log.error(msg); output.append(msg)
    return result


def sd_insert_to_guide(db_path: str, schedules: list, programs: dict, output: list):
    inserted = 0
    skipped = 0
    matched = 0

    con = get_conn(db_path)
    try:
        # Load existing guide keys
        existing_keys = set()
        for row in con.execute("SELECT channel, start_utc FROM guide"):
            existing_keys.add(f"{row[0]}\t{row[1]}")

        # Load master title lookup
        master_by_norm = {}
        master_by_norm_year = {}
        for row in con.execute(
            "SELECT id, normalized_title, year FROM master_titles"
        ):
            if not row["normalized_title"]:
                continue
            norm = row["normalized_title"]
            mid = row["id"]
            if norm not in master_by_norm:
                master_by_norm[norm] = mid
            if row["year"]:
                year_key = f"{norm}\t{row['year']}"
                if year_key not in master_by_norm_year:
                    master_by_norm_year[year_key] = mid

        msg = f"SD → master_titles loaded: {len(master_by_norm)} entries."
        log.info(msg); output.append(msg)

        sql = """
            INSERT OR IGNORE INTO guide
                (title, normalized_title, channel, start_utc, end_utc, xml_file,
                 master_title_id, program_type, season_number, episode_number,
                 episode_title, year)
            VALUES (?,?,?,?,?,'schedules_direct',?,?,?,?,?,?)
        """
        rows = []
        total = len(schedules)
        for idx, s in enumerate(schedules):
            if idx > 0 and idx % 10000 == 0:
                msg = f"SD → InsertToGuide progress: {idx}/{total}"
                log.info(msg); output.append(msg)

            p = programs.get(s["program_id"])
            if not p or not p.get("title"):
                skipped += 1
                continue

            start_str = s["air_datetime"].strftime("%Y-%m-%d %H:%M:%S")
            end_str = (s["air_datetime"] + timedelta(seconds=s["duration"])).strftime(
                "%Y-%m-%d %H:%M:%S"
            )
            channel_id = f"sd.{s['station_id']}"
            norm_title = normalize_title(p["title"])
            guide_key = f"{channel_id}\t{start_str}"

            if guide_key in existing_keys:
                skipped += 1
                continue

            # Match master title
            master_id = None
            if p["movie_year"] and f"{norm_title}\t{p['movie_year']}" in master_by_norm_year:
                master_id = master_by_norm_year[f"{norm_title}\t{p['movie_year']}"]
                matched += 1
            elif norm_title in master_by_norm:
                master_id = master_by_norm[norm_title]
                matched += 1

            rows.append((
                p["title"],
                norm_title,
                channel_id,
                start_str,
                end_str,
                master_id,
                p["program_type"] or None,
                p["season_number"] or None,
                p["episode_number"] or None,
                p["episode_title"] or None,
                int(p["movie_year"]) if p["movie_year"] else None,
            ))
            existing_keys.add(guide_key)
            inserted += 1

            if len(rows) >= 5000:
                con.executemany(sql, rows)
                con.commit()
                rows = []

        if rows:
            con.executemany(sql, rows)
            con.commit()

        msg = f"SD → Inserted: {inserted} | Skipped: {skipped} | Matched: {matched}"
        log.info(msg); output.append(msg)
    finally:
        con.close()


def sd_update_master_title_types(db_path: str, programs: dict, output: list):
    con = get_conn(db_path)
    try:
        updated = 0
        with con:
            for p in programs.values():
                if not p.get("title"):
                    continue
                norm = normalize_title(p["title"])
                con.execute("""
                    UPDATE master_titles
                    SET is_movie=?, is_series=?
                    WHERE normalized_title=?
                """, (
                    1 if p["program_type"] == "MV" else 0,
                    1 if p["program_type"] == "EP" else 0,
                    norm,
                ))
                updated += 1
        msg = f"SD → master_titles types updated ({updated} processed)."
        log.info(msg); output.append(msg)
    finally:
        con.close()


def update_sd_guide(config: dict, db_path: str, output: list):
    sd_user = config.get("SD_USER", "")
    sd_pass = config.get("SD_PASS", "")
    if not sd_user or not sd_pass:
        msg = "SD → No SD_USER/SD_PASS in config, skipping."
        log.info(msg); output.append(msg)
        return

    # Load lineups
    lineups_raw = config.get("SD_LINEUPS", config.get("SD_LINEUP", DEFAULT_SD_LINEUP))
    if isinstance(lineups_raw, list):
        lineups = [l.strip() for l in lineups_raw if l.strip()]
    else:
        lineups = [l.strip() for l in lineups_raw.split(",") if l.strip()]
    if not lineups:
        lineups = [DEFAULT_SD_LINEUP]
    lineups = list(dict.fromkeys(lineups))  # dedupe

    if not sd_authenticate(sd_user, sd_pass, output):
        return

    channels, station_names = sd_get_lineup_channels(lineups, output)
    if not channels:
        msg = "SD → No channels returned."
        log.warning(msg); output.append(msg)
        return

    sd_sync_stations_to_channels(db_path, channels, station_names, output)

    schedules = sd_get_schedules(list(channels.keys()), output)
    if not schedules:
        msg = "SD → No schedules returned."
        log.warning(msg); output.append(msg)
        return

    # Incremental cutoff
    cutoff = sd_get_incremental_cutoff(db_path)
    if cutoff:
        before = len(schedules)
        schedules = [s for s in schedules if s["air_datetime"] >= cutoff]
        msg = f"SD → Incremental cutoff {cutoff}: kept {len(schedules)}/{before} schedules."
        log.info(msg); output.append(msg)

    schedules = sd_filter_existing_schedules(db_path, schedules)
    if not schedules:
        msg = "SD → No missing schedules to import."
        log.info(msg); output.append(msg)
        return

    program_ids = list({s["program_id"] for s in schedules})
    programs = sd_get_programs(program_ids, output)

    sd_insert_to_guide(db_path, schedules, programs, output)
    sd_update_master_title_types(db_path, programs, output)


# ── Map SD to PS channels ─────────────────────────────────────────────────────

def map_sd_to_ps_channels(db_path: str, output: list):
    msg = "Mapping SD guide to PS channels..."
    log.info(msg); output.append(msg)
    con = get_conn(db_path)
    try:
        cur = con.execute("""
            INSERT OR IGNORE INTO guide
                (title, normalized_title, channel, start_utc, end_utc, xml_file,
                 program_type, season_number, episode_number, episode_title, year)
            SELECT
                g.title, g.normalized_title,
                c.channel_id,
                g.start_utc, g.end_utc, 'sd_mapped',
                g.program_type, g.season_number, g.episode_number, g.episode_title, g.year
            FROM guide g
            JOIN channels c ON c.sd_station_id = g.channel
            LEFT JOIN guide ps ON ps.channel = c.channel_id
                AND ps.start_utc = g.start_utc
            WHERE g.xml_file = 'schedules_direct'
            AND ps.id IS NULL
            AND c.stream_id IS NOT NULL
            AND c.stream_id != 0
        """)
        con.commit()
        msg = f"SD mapped → {cur.rowcount} rows inserted to PS channels."
        log.info(msg); output.append(msg)
    except Exception as e:
        msg = f"MapSDToPSChannels error: {e}"
        log.error(msg); output.append(msg)
    finally:
        con.close()


# ── Refresh stream IDs ────────────────────────────────────────────────────────

def refresh_stream_ids(config: dict, db_path: str, output: list):
    msg = "Refreshing provider stream IDs..."
    log.info(msg); output.append(msg)
    try:
        epg_url = config["EPG_BASE_URL"]
        epg_user = config["EPG_USER"]
        epg_pass = config["EPG_PASS"]
        user_agent = config.get("USER_AGENT_TM", config.get("USER_AGENT", ""))

        r = requests.get(
            f"{epg_url}/player_api.php",
            params={"username": epg_user, "password": epg_pass, "action": "get_live_streams"},
            headers={"User-Agent": user_agent} if user_agent else {},
            timeout=60,
        )
        streams = r.json()
        if isinstance(streams, dict) and "error" in streams:
            msg = f"RefreshStreamIds bad response: {streams}"
            log.warning(msg); output.append(msg)
            return

        updated = 0
        skipped = 0
        con = get_conn(db_path)
        try:
            with con:
                for stream in streams:
                    stream_id = stream.get("stream_id")
                    if stream_id is None:
                        skipped += 1
                        continue
                    epg_id = stream.get("epg_channel_id")
                    name = stream.get("name")
                    if epg_id:
                        cur = con.execute(
                            "UPDATE channels SET stream_id=? WHERE guide_channel=?",
                            (stream_id, epg_id),
                        )
                    elif name:
                        cur = con.execute(
                            "UPDATE channels SET stream_id=? WHERE nickname=?",
                            (stream_id, name),
                        )
                    else:
                        skipped += 1
                        continue
                    updated += cur.rowcount
        finally:
            con.close()

        msg = f"Stream IDs refreshed: {updated} updated, {skipped} skipped."
        log.info(msg); output.append(msg)
    except Exception as e:
        msg = f"RefreshStreamIds error: {e}"
        log.error(msg); output.append(msg)


# ── Download guide XML ────────────────────────────────────────────────────────

def download_guide(url: str, local_path: str, user_agent: str, output: list) -> bool:
    try:
        time.sleep(__import__("random").randint(1, 10))
        headers = {"User-Agent": user_agent} if user_agent else {}
        r = requests.get(url, headers=headers, timeout=300, stream=True)
        if r.status_code == 401:
            msg = "Guide download blocked (401) — check credentials."
            log.error(msg); output.append(msg)
            return False
        if r.status_code == 429:
            msg = "Guide download rate limited (429)."
            log.warning(msg); output.append(msg)
            return False
        r.raise_for_status()
        with open(local_path, "wb") as f:
            for chunk in r.iter_content(chunk_size=65536):
                f.write(chunk)
        msg = "Guide downloaded successfully."
        log.info(msg); output.append(msg)
        return True
    except Exception as e:
        msg = f"Guide download failed: {e}"
        log.error(msg); output.append(msg)
        return False


# ── Main import orchestrator (GuideUpdater.UpdateGuide) ───────────────────────

def run_import(force: bool = False, source: str = "manual"):
    global _import_status

    output = []
    started = datetime.now().isoformat()

    try:
        config = load_config()
        db_path = config["DB_PATH"]
        guide_dir = config["GUIDE_DATA_DIR"]
        epg_url = config["EPG_BASE_URL"]
        epg_xmltv = config["EPG_XMLTV"]
        epg_user = config["EPG_USER"]
        epg_pass = config["EPG_PASS"]
        user_agent = config.get("USER_AGENT_TM", config.get("USER_AGENT", ""))

        os.makedirs(guide_dir, exist_ok=True)

        stamp_file = os.path.join(guide_dir, "last_import.txt")
        local_xml = os.path.join(guide_dir, "guide.xml")
        guide_url = f"{epg_url}{epg_xmltv}?username={epg_user}&password={epg_pass}"

        needs_import = (
            force
            or guide_needs_update(guide_dir, stamp_file)
            or guide_db_is_empty(db_path)
        )

        version = "EPG SERVER v" + VERSION
        header = f"{version} | STARTED: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}"
        output.append(header)
        log.info(header)

        if needs_import:
            backup_database(db_path, "pre_import", config, output)
            if not download_guide(guide_url, local_xml, user_agent, output):
                raise Exception("Guide download failed")

            if not os.path.exists(local_xml) or os.path.getsize(local_xml) < 1000:
                raise Exception("Guide download failed or empty")

            rebuild_guide_database(db_path, output)

            update_sd_guide(config, db_path, output)

            import_xml(local_xml, db_path, output)
            create_guide_indexes(db_path, output)
            map_sd_to_ps_channels(db_path, output)

            save_update_stamp(guide_dir, stamp_file)
            backup_database(db_path, "post_import", config, output)

            msg = "Guide update complete."
            log.info(msg); output.append(msg)
        else:
            msg = "Guide unchanged — skipping download."
            log.info(msg); output.append(msg)

        refresh_stream_ids(config, db_path, output)

        with _import_lock:
            _import_status.update({
                "status": "completed",
                "finished_at": datetime.now().isoformat(),
                "last_error": None,
                "last_output": "\n".join(output),
            })

    except Exception as e:
        error_msg = str(e)
        log.error(f"Import failed: {error_msg}")
        output.append(f"FATAL ERROR:\n{error_msg}")
        with _import_lock:
            _import_status.update({
                "status": "failed",
                "finished_at": datetime.now().isoformat(),
                "last_error": f"guide import output contained a fatal database error"
                if "database" in error_msg.lower()
                else error_msg,
                "last_output": "\n".join(output),
            })


# ── Flask routes ──────────────────────────────────────────────────────────────

@app.route("/guide_import/trigger", methods=["POST"])
def trigger_import():
    with _import_lock:
        if _import_status["status"] == "running":
            return jsonify({"ok": False, "error": "Import already running"}), 409
        _import_status.update({
            "status": "running",
            "started_at": datetime.now().isoformat(),
            "finished_at": None,
            "last_error": None,
            "last_output": [],
            "pid": os.getpid(),
            "requested": True,
            "source": "manual",
            "force": False,
        })

    thread = threading.Thread(target=run_import, kwargs={"source": "manual"}, daemon=True)
    thread.start()
    return jsonify({"ok": True, "message": "Import started"})


@app.route("/guide_import/status")
def import_status():
    with _import_lock:
        return jsonify(dict(_import_status))


@app.route("/health")
def health():
    return jsonify({"ok": True, "version": VERSION})


# ── Scheduled import (runs at startup + every 24h) ────────────────────────────

def scheduled_import_loop():
    # Wait 60s after startup before first run
    time.sleep(60)
    while True:
        log.info("Scheduled import starting...")
        with _import_lock:
            _import_status.update({
                "status": "running",
                "started_at": datetime.now().isoformat(),
                "finished_at": None,
                "last_error": None,
                "last_output": [],
                "pid": os.getpid(),
                "requested": True,
                "source": "scheduled",
                "force": False,
            })
        run_import(source="scheduled")
        time.sleep(24 * 60 * 60)  # 24 hours
_db_proxy_lock = threading.Lock()
def _get_db_path() -> str:
    """Get current DB path from config."""
    try:
        config = load_config()
        return config["DB_PATH"]
    except Exception:
        return "/share/EPG/Movies.db"
def _serialize_rows(rows) -> list:
    """Convert sqlite3.Row list to JSON-serializable list of dicts."""
    result = []
    for row in rows:
        d = {}
        for key in row.keys():
            val = row[key]
            if isinstance(val, (int, float, str, type(None))):
                d[key] = val
            else:
                d[key] = str(val)
        result.append(d)
    return result
@app.route("/db/execute", methods=["POST"])
def db_execute():
    """
    Execute one or more SQL statements against Movies.db.
    Request JSON:
    {
        "statements": [
            {
                "sql": "SELECT * FROM scheduled_recordings WHERE status=?",
                "params": ["scheduled"],
                "fetch": "all"   // "all", "one", or "none"
            },
            ...
        ],
        "transaction": true   // wrap all in a single transaction (default: true)
    }
    Response JSON:
    {
        "ok": true,
        "results": [
            {
                "rows": [...],      // for SELECT
                "rowcount": 1,      // for INSERT/UPDATE/DELETE
                "lastrowid": 42     // for INSERT
            },
            ...
        ]
    }
    """
    try:
        body = request.get_json(force=True)
        if not body or "statements" not in body:
            return jsonify({"ok": False, "error": "Missing 'statements'"}), 400
        statements = body["statements"]
        use_transaction = body.get("transaction", True)
        db_path = _get_db_path()
        results = []
        with _db_proxy_lock:
            con = sqlite3.connect(db_path, timeout=30)
            con.row_factory = sqlite3.Row
            con.execute("PRAGMA busy_timeout=30000")
            con.execute("PRAGMA journal_mode=WAL")
            try:
                if use_transaction:
                    con.execute("BEGIN")
                for stmt in statements:
                    sql = stmt.get("sql", "").strip()
                    params = stmt.get("params", [])
                    fetch = stmt.get("fetch", "none").lower()
                    # Convert list params to tuple
                    if isinstance(params, dict):
                        cur = con.execute(sql, params)
                    else:
                        cur = con.execute(sql, params)
                    result = {
                        "rowcount": cur.rowcount,
                        "lastrowid": cur.lastrowid,
                    }
                    if fetch == "all":
                        result["rows"] = _serialize_rows(cur.fetchall())
                    elif fetch == "one":
                        row = cur.fetchone()
                        result["rows"] = [_serialize_rows([row])[0]] if row else []
                    else:
                        result["rows"] = []
                    results.append(result)
                if use_transaction:
                    con.execute("COMMIT")
            except Exception as e:
                if use_transaction:
                    try:
                        con.execute("ROLLBACK")
                    except Exception:
                        pass
                con.close()
                log.error(f"DB proxy execute error: {e}")
                return jsonify({"ok": False, "error": str(e)}), 500
            con.close()
        return jsonify({"ok": True, "results": results})
    except Exception as e:
        log.error(f"DB proxy error: {e}")
        return jsonify({"ok": False, "error": str(e)}), 500
@app.route("/db/ping")
def db_ping():
    """Quick check that the DB is accessible."""
    try:
        db_path = _get_db_path()
        con = sqlite3.connect(db_path, timeout=5)
        con.execute("SELECT 1")
        con.close()
        return jsonify({"ok": True, "db_path": db_path})
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)}), 500


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    log.info(f"EPG Server v{VERSION} starting on port {PORT}")
    log.info(f"Config: {CONFIG_PATH}")

    threading.Thread(target=scheduled_import_loop, daemon=True).start()
    app.run(host="0.0.0.0", port=PORT, debug=False)
