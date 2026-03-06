' [2026-03-02] EPG Watch v10.55 - UNIFIED MASTER VERSION
' Merged: v10.54 + User's Archiving Download + Full History Logic
' CONSTRAINTS: No " in SQL, ordered reports, Direct ADB Argument Injection.
' FIXES: PowerShell quote-mangling and "While:End While" shorthand errors.

Imports Microsoft.Data.Sqlite
Imports System.IO
Imports System.Text.Json
Imports System.Xml
Imports System.Text.RegularExpressions
Imports System.Diagnostics
Imports System.Net.Http
Imports System.Runtime.InteropServices
Imports System.Threading
Imports Newtonsoft.Json.Linq

Module Program
    ' --- CONFIG CONSTANTS ---
    Private Const MY_CONFIG As String = "/Users/garyscudder/epg/config.json"
    ' --- GLOBAL VARIABLES ---
    Private _nasIp As String = ""
    Private _firestickIp As String = ""
    Private _adbExePath As String = ""
    Private _DbPath As String = ""
    Private _HistPath As String = ""
    Private _nasWarehouseDir As String = ""
    Private _guideDir As String = ""
    Private _guidedb As String = ""
    Public _epgUrl As String = ""
    Public _epgXMLTV As String = ""
    Public _epgUser As String = ""
    Public _epgPass As String = ""
    Public _plexMoviesPath As String = ""
    Public _ffmpegPath As String = ""
    Public _userAgent As String = ""
    Private _stickLanding As String = ""

    Private _preferredChannels As New HashSet(Of String)
    Private ReadOnly _localDir As String = "C:\Movies\"
    Private _localDb As String = ""
    Private _localHist As String = ""

    Sub Main(args As String())
        Dim version = Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()

        Console.Title = "EPG Manager v" & version
        Console.WriteLine("EPG MANAGER v" & version & " | STARTED: " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))

        Dim sw As Stopwatch = Stopwatch.StartNew()

        Try
            If Not LoadConfig() Then
                Console.WriteLine("Could not load config.json at " & MY_CONFIG)
                Console.ReadLine()
                Return
            End If

            ' ---------------------------------------------------
            ' 1️⃣ UPDATE STREAM IDS
            ' ---------------------------------------------------
            Dim localMoviesDb = DbCache.GetLocalCopy(_DbPath)

            Console.WriteLine("Original DB: " & _DbPath)
            Console.WriteLine("Local DB copy: " & localMoviesDb)

            If File.Exists(localMoviesDb) Then
                Console.WriteLine("Local DB size: " & New FileInfo(localMoviesDb).Length & " bytes")
            Else
                Console.WriteLine("Local DB DOES NOT EXIST")
            End If

            Console.WriteLine("Updating stream IDs...")

            Dim json = GetXtreamJson(_epgUrl, _epgUser, _epgPass, _userAgent).Result

            Dim streams =
            Newtonsoft.Json.JsonConvert.DeserializeObject(Of List(Of XtreamStream))(json)

            UpdateStreamIds(_epgUrl, _epgUser, _epgPass, streams, localMoviesDb)

            Console.WriteLine("Stream ID update complete.")


            ' ---------------------------------------------------
            ' 2️⃣ GUIDE BUILD
            ' ---------------------------------------------------

            Dim localGuideDb = _guidedb

            Dim stampFile = Path.Combine(_guideDir, "last_import.txt")

            Dim guideUrl =
            $"{_epgUrl}{_epgXMLTV}?username={_epgUser}&password={_epgPass}"

            Dim localPath = Path.Combine(_guideDir, "guide.xml")

            Console.WriteLine()
            Console.WriteLine("Downloading XML guide...")

            DownloadGuideProperly(guideUrl, localPath).Wait()

            Dim needsImport As Boolean =
            GuideUpdateDetector.GuideNeedsUpdate(_guideDir, stampFile) _
            OrElse GuideDbIsEmpty(localGuideDb)

            If needsImport Then

                Console.WriteLine("Rebuilding guide database")

                RebuildGuideDatabase(localGuideDb)

                Console.WriteLine("Importing XML guide")

                For Each xmlFile In Directory.GetFiles(_guideDir, "*.xml")
                    GuideImporter.ImportXml(xmlFile, localGuideDb)
                Next

                Console.WriteLine("Creating guide indexes")

                CreateGuideIndexes(localGuideDb)

                GuideUpdateDetector.SaveUpdateStamp(_guideDir, stampFile)

            Else

                Console.WriteLine("Guide unchanged → skipping import")

            End If

            ' ---------------------------------------------------
            ' 3️⃣ SUGGESTIONS ENGINE
            ' ---------------------------------------------------

            Dim localHistoryDb = _HistPath

            Dim stats As New EngineStats

            Dim candidates =
            GuideQueryEngine.GetUpcomingCandidates(
                localGuideDb,
                localHistoryDb,
                localMoviesDb,
                stats)

            Console.WriteLine("Candidates found: " & candidates.Count)

            Dim scored = RecommendationEngine.ScoreAll(candidates)

            Console.WriteLine()
            Console.WriteLine("===== SUGGESTIONS =====")
            Console.WriteLine()
            Console.WriteLine($"{"Score",5} {"Start",-18} {"Channel",-24} {"Title",-35} {"Nick",-10} {"MyCh",-5}")
            Console.WriteLine(New String("-"c, 100))

            Dim myChannels = LoadMyChannels(localMoviesDb)
            Console.WriteLine("Movie channels loaded: " & myChannels.Count)

            Dim planned = scored _
.Where(Function(x) myChannels.Contains(x.Candidate.Channel)) _
.Where(Function(x) Not ChannelLookup.IsForeign(localMoviesDb, x.Candidate.Channel)) _
.Where(Function(x) ChannelLookup.IsMovieChannel(localMoviesDb, x.Candidate.Channel)) _
.Where(Function(x) x.Candidate.StartTime > DateTime.Now) _
.GroupBy(Function(x) NormalizeTitle(x.Candidate.Title)) _
.Select(Function(g) g _
    .OrderByDescending(Function(m) TitleHelpers.GetChannelPriority(m.Candidate.Channel)) _
    .ThenByDescending(Function(m) m.Candidate.Channel.ToLower().Contains("hd")) _
    .ThenBy(Function(m) m.Candidate.StartTime) _
    .First()) _
.OrderBy(Function(x) x.Candidate.StartTime) _
.Take(100)
            Dim recordingLog As New List(Of String)
            Dim started As New HashSet(Of String)

            While True

                Console.Clear()

                Console.WriteLine("EPG MANAGER DVR")
                Console.WriteLine("---------------------------------------------------------------")
                Console.WriteLine($"Now: {DateTime.Now:dddd MMM d HH:mm:ss}")
                Console.WriteLine()

                Console.WriteLine("NEXT RECORDINGS")
                Console.WriteLine("---------------------------------------------------------------")
                Console.WriteLine("Start   Channel                 Title                          In")
                Console.WriteLine("---------------------------------------------------------------")
                Console.WriteLine()
                Console.WriteLine("ACTIVE / STARTED RECORDINGS")
                Console.WriteLine("---------------------------------------------------------------")

                For Each r In recordingLog
                    Console.ForegroundColor = ConsoleColor.Green
                    Console.WriteLine(r)
                Next

                Console.ResetColor()
                For Each s In planned.Take(10)

                    Dim key = s.Candidate.Channel & "|" & s.Candidate.StartTime

                    Dim diff = (s.Candidate.StartTime - DateTime.Now).TotalSeconds

                    If diff <= 30 AndAlso diff >= -30 AndAlso Not started.Contains(key) Then

                        started.Add(key)

                        Dim streamId = ChannelLookup.GetStreamId(localMoviesDb, s.Candidate.Channel)

                        If String.IsNullOrWhiteSpace(streamId) Then
                            Continue For
                        End If

                        Recorder.RecordMovie(
        s.Candidate.Title,
        streamId,
        s.Candidate.StartTime,
        s.Candidate.EndTime)

                        Dim ch = ChannelLookup.GetChannelInfo(localMoviesDb, s.Candidate.Channel)

                        Dim msg = $"▶ RECORDING NOW → {DateTime.Now:HH:mm:ss} | {ch.Item1} | {s.Candidate.Title}"

                        recordingLog.Add(msg)

                    End If

                Next

                Thread.Sleep(5000)

            End While

            Console.ResetColor()

            sw.Stop()

            Console.WriteLine(vbCrLf & "--------------------------------------------------------------------------------")
            Console.WriteLine()
            Console.WriteLine("Scheduler active... waiting for start times")

            While True
                Thread.Sleep(1000)
            End While
            Console.WriteLine("DONE. Time: " & sw.Elapsed.Minutes & "m " & sw.Elapsed.Seconds & "s")

            Console.WriteLine("Press Enter to exit.")
            Console.ReadLine()

        Catch ex As Exception
            Console.WriteLine("FATAL ERROR:")
            Console.WriteLine(ex.ToString())
        End Try
    End Sub

    ' --- DOWNLOAD LOGIC (USER ARCHIVE VERSION) ---
    Async Function DownloadGuideProperly(url As String, localPath As String) As Task

        Dim handler As New HttpClientHandler()
        handler.AutomaticDecompression =
        Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate

        Using client As New HttpClient(handler)

            client.DefaultRequestHeaders.Clear()

            client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")

            client.DefaultRequestHeaders.Add("Accept",
            "text/xml,application/xml;q=0.9,*/*;q=0.8")

            client.DefaultRequestHeaders.Add("Accept-Language",
            "en-US,en;q=0.9")

            client.DefaultRequestHeaders.Add("Connection", "keep-alive")

            Dim response = Await client.GetAsync(url)

            response.EnsureSuccessStatusCode()

            Dim bytes = Await response.Content.ReadAsByteArrayAsync()

            ' Safety check to prevent saving HTML
            Dim textCheck = System.Text.Encoding.UTF8.GetString(bytes)

            If textCheck.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) Then
                Throw New Exception("Guide returned HTML instead of XML.")
            End If

            File.WriteAllBytes(localPath, bytes)

            Console.WriteLine("[DL] Guide downloaded successfully.")

        End Using

    End Function
    ' --- GUIDE PROCESSING ---
    Sub ProcessGuideXmls()
        Dim xmlFiles = Directory.GetFiles(_guideDir, "*.xml").Where(Function(f) (DateTime.Now - File.GetLastWriteTime(f)).TotalHours < 24).ToArray()
        If xmlFiles.Length = 0 Then Return
        Dim keys As New HashSet(Of String)
        Using conn As New SqliteConnection("Data Source=" & _localHist & ";Version=3;")
            conn.Open()
            Using cmd = New SqliteCommand("SELECT title || start || channel FROM recording_history", conn)
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        keys.Add(rdr(0).ToString())
                    End While
                End Using
            End Using
            Using trans = conn.BeginTransaction()
                For Each file In xmlFiles
                    Dim nick = If(Path.GetFileName(file).ToLower() = "guide.xml", "Source_PrimeStreams", Path.GetFileName(file))
                    DrawProgressBar(Array.IndexOf(xmlFiles, file) + 1, xmlFiles.Length, "[XML] " & nick)
                    Dim doc As New XmlDocument()
                    doc.Load(file)
                    For Each node As XmlNode In doc.SelectNodes("//programme")
                        Dim t = node.SelectSingleNode("title")?.InnerText
                        Dim s = node.Attributes("start")?.Value.Substring(0, 14)
                        Dim c = node.Attributes("channel")?.Value
                        If t Is Nothing Or s Is Nothing Or c Is Nothing OrElse keys.Contains(t & s & c) Then Continue For
                        Using cmdIns = New SqliteCommand("INSERT INTO recording_history (title, desc, channel, start, stop, xml_source, my_channel) VALUES (@t, @d, @c, @s, @e, @x, @m)", conn)
                            cmdIns.Parameters.AddWithValue("@t", t)
                            cmdIns.Parameters.AddWithValue("@d", node.SelectSingleNode("desc")?.InnerText)
                            cmdIns.Parameters.AddWithValue("@c", c)
                            cmdIns.Parameters.AddWithValue("@s", s)
                            cmdIns.Parameters.AddWithValue("@e", node.Attributes("stop").Value.Substring(0, 14))
                            cmdIns.Parameters.AddWithValue("@x", nick)
                            cmdIns.Parameters.AddWithValue("@m", If(_preferredChannels.Contains(c), 1, 0))
                            cmdIns.ExecuteNonQuery()
                        End Using
                        keys.Add(t & s & c)
                    Next
                Next
                trans.Commit()
            End Using
        End Using
    End Sub

    Sub GenerateUpcomingPremiumReport(liveMap As Dictionary(Of String, String))
        Console.WriteLine(vbCrLf & "📡 SCANNING PREMIUM ENGLISH CHANNELS (Next 24h)...")
        Dim nowStr As String = DateTime.Now.ToString("yyyyMMddHHmmss")
        Dim upcoming As New List(Of Dictionary(Of String, Object))
        Dim premiums As String() = {"hbo", "sho", "max", "starz", "epix", "mgm", "tmc", "cinemax", "showtime"}
        Dim exclude As String() = {"newsmax", "shopping", "hsn", "qvc", "latino", "espanol"}

        For Each xmlPath In Directory.GetFiles(_guideDir, "*.xml")
            Try
                Dim doc As New XmlDocument()
                doc.Load(xmlPath)
                For Each node As XmlNode In doc.SelectNodes("//programme")
                    Dim chanId = (If(node.Attributes("channel")?.Value, "")).ToLower()
                    If premiums.Any(Function(p) chanId.Contains(p)) AndAlso Not exclude.Any(Function(x) chanId.Contains(x)) Then
                        Dim startRaw = node.Attributes("start")?.Value.Substring(0, 14)
                        If startRaw > nowStr Then
                            Dim startDt = DateTime.ParseExact(startRaw, "yyyyMMddHHmmss", Nothing)
                            Dim stopDt = DateTime.ParseExact(node.Attributes("stop").Value.Substring(0, 14), "yyyyMMddHHmmss", Nothing)
                            Dim duration = CInt((stopDt - startDt).TotalMinutes)
                            Dim cleanId = Regex.Replace(chanId.Split("."c)(0).ToUpper(), "[^A-Z0-9]", "")
                            Dim chNum = If(liveMap.ContainsKey(cleanId), liveMap(cleanId), "---")

                            upcoming.Add(New Dictionary(Of String, Object) From {
                                {"sort", startRaw}, {"start", startDt.ToString("MM/dd hh:mm tt")},
                                {"num", chNum}, {"chan", chanId.ToUpper()},
                                {"title", node.SelectSingleNode("title")?.InnerText.Trim()}, {"dur", duration},
                                {"end", stopDt.ToString("hh:mm tt")}
                            })
                        End If
                    End If
                Next
            Catch : End Try
        Next

        upcoming = upcoming.OrderBy(Function(x) x("sort")).ToList()
        Dim seen As New HashSet(Of String)
        Console.WriteLine(String.Format("{0,-18} | {1,-10} | {2,-5} | {3,-5} | {4,-18} | {5}", "START TIME", "FINISH", "DUR", "CH#", "CHANNEL", "TITLE"))
        Console.WriteLine(New String("-"c, 110))

        Using conn As New SqliteConnection("Data Source=" & _localHist & ";Version=3;")
            conn.Open()
            For Each m In upcoming
                Dim key = m("title").ToString() & m("start").ToString()
                If Not seen.Contains(key) Then
                    Using cmd = New SqliteCommand("SELECT 1 FROM recording_history WHERE title = @t AND owned = 1 LIMIT 1", conn)
                        cmd.Parameters.AddWithValue("@t", m("title"))
                        Dim isOwned = (cmd.ExecuteScalar() IsNot Nothing)
                        If isOwned Then Console.ForegroundColor = ConsoleColor.DarkGray Else Console.ForegroundColor = ConsoleColor.White
                        Dim cleanChan = m("chan").ToString().Replace("US|", "").Replace("UK|", "")
                        If cleanChan.Length > 17 Then cleanChan = cleanChan.Substring(0, 17)
                        Console.WriteLine(String.Format("{0,-18} | {1,-10} | {2,3}m  | {3,-5} | {4,-18} | {5}{6}{7}",
                            m("start"), m("end"), m("dur"), m("num"), cleanChan, If(CInt(m("dur")) >= 140, "🚩 ", "   "), m("title"), If(isOwned, " [OWNED]", "")))
                        Console.ResetColor()
                        seen.Add(key)
                    End Using
                End If
            Next
        End Using
    End Sub

    ' --- DATABASE & SYSTEM HELPERS ---
    Sub SyncFromNas()
        Try
            If Not Directory.Exists(_localDir) Then Directory.CreateDirectory(_localDir)
            File.Copy(_DbPath, _localDb, True)
            File.Copy(_HistPath, _localHist, True)
        Catch : End Try
    End Sub

    Sub SyncToNas()
        Try
            File.Copy(_localDb, _DbPath, True)
            File.Copy(_localHist, _HistPath, True)
        Catch : End Try
    End Sub

    Sub EnsureOwnedColumnExists()
        Using conn As New SqliteConnection("Data Source=" & _localHist & ";Version=3;")
            conn.Open()
            Dim hasOwned = False
            Using cmd = New SqliteCommand("PRAGMA table_info(recording_history)", conn)
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        If rdr("name").ToString().ToLower() = "owned" Then hasOwned = True
                    End While
                End Using
            End Using
            If Not hasOwned Then
                Using cmdAlt = New SqliteCommand("ALTER TABLE recording_history ADD COLUMN owned INTEGER DEFAULT 0", conn)
                    cmdAlt.ExecuteNonQuery()
                End Using
            End If
        End Using
    End Sub

    Sub LoadPreferredChannels()
        _preferredChannels.Clear()
        Using conn As New SqliteConnection("Data Source=" & _localDb & ";Version=3;")
            conn.Open()
            Using cmd = New SqliteCommand("SELECT channel_id FROM channels WHERE my_channel > 0", conn)
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        _preferredChannels.Add(rdr(0).ToString())
                    End While
                End Using
            End Using
        End Using
    End Sub

    Sub EvaluateOwnedMovies()
        Console.WriteLine(vbCrLf & "[MDB] Syncing Master Database...")
        Using conn As New SqliteConnection("Data Source=" & _localHist & ";Version=3;")
            conn.Open()
            Using cmdAttach = New SqliteCommand("ATTACH DATABASE '" & _localDb & "' AS mdb", conn) : cmdAttach.ExecuteNonQuery() : End Using
            Dim count = New SqliteCommand("UPDATE recording_history SET owned = 1 WHERE UPPER(title) IN (SELECT UPPER(title) FROM mdb.master_titles)", conn).ExecuteNonQuery()
            Console.WriteLine("      Updated " & count & " records.")
            Using cmdDetach = New SqliteCommand("DETACH DATABASE mdb", conn) : cmdDetach.ExecuteNonQuery() : End Using
        End Using
    End Sub

    Sub PrintNasStorageStats()
        Console.WriteLine(vbCrLf & "[NAS] Storage Stats")
        Try
            Dim dInfo As New DriveInfo(Path.GetPathRoot(_nasWarehouseDir))
            If dInfo.IsReady Then
                Dim freeGB = dInfo.TotalFreeSpace / 1024 / 1024 / 1024
                Dim totalGB = dInfo.TotalSize / 1024 / 1024 / 1024
                Console.WriteLine("      Free: " & freeGB.ToString("N0") & " GB / " & totalGB.ToString("N0") & " GB (" & (((totalGB - freeGB) / totalGB) * 100).ToString("F1") & "% used)")
            End If
        Catch : End Try
    End Sub

    Sub UpdateHistoryFromNasFiles()
        If Not Directory.Exists(_nasWarehouseDir) Then Return
        Using conn As New SqliteConnection("Data Source=" & _localHist & ";Version=3;")
            conn.Open()
            For Each filePath In Directory.GetFiles(_nasWarehouseDir, "*.ts")
                Dim fn = Path.GetFileName(filePath)
                Dim parts = Regex.Replace(fn, "-Copy\(\d+\)", "").Split("_"c)
                If parts.Length >= 3 Then
                    Dim title = String.Join(" ", parts.Take(parts.Length - 2)).Replace("_", " ")
                    Dim time = parts(parts.Length - 2) & parts(parts.Length - 1).Replace(".ts", "")
                    Using cmdUpd = New SqliteCommand("UPDATE recording_history SET status = 'recorded', filename = @fn, file_size = @fs, timestamp = @ts WHERE UPPER(title) = @t AND start LIKE @s AND status = ''", conn)
                        cmdUpd.Parameters.AddWithValue("@fn", fn)
                        cmdUpd.Parameters.AddWithValue("@fs", (New FileInfo(filePath).Length / 1024 / 1024).ToString("F0") & " MB")
                        cmdUpd.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                        cmdUpd.Parameters.AddWithValue("@t", title.ToUpper())
                        cmdUpd.Parameters.AddWithValue("@s", time & "%")
                        cmdUpd.ExecuteNonQuery()
                    End Using
                End If
            Next
        End Using
    End Sub
    Function LoadConfig() As Boolean

        Try

            If Not File.Exists(MY_CONFIG) Then
                Console.WriteLine("Config not found: " & MY_CONFIG)
                Return False
            End If

            Dim root =
            JsonDocument.Parse(File.ReadAllText(MY_CONFIG)).RootElement

            _nasIp = root.GetProperty("MY_NAS_IP").GetString()
            _firestickIp = root.GetProperty("FIRESTICK_IP").GetString()
            _adbExePath = If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), root.GetProperty("ADB_WIN_PATH").GetString(), root.GetProperty("ADB_MAC_PATH").GetString())
            _stickLanding = root.GetProperty("STICK_LANDING").GetString()
            _DbPath = root.GetProperty("DB_PATH").GetString()
            _HistPath = root.GetProperty("DB_HIST_PATH").GetString()
            _guideDir = root.GetProperty("GUIDE_DATA_DIR").GetString()
            _guidedb = root.GetProperty("GUIDE_MASTER_DB").GetString()
            Console.WriteLine("Guide DB path: " & _guidedb)
            _nasWarehouseDir = root.GetProperty("WAREHOUSE").GetString()
            If root.TryGetProperty("EPG_BASE_URL", Nothing) Then _epgUrl = root.GetProperty("EPG_BASE_URL").GetString()
            If root.TryGetProperty("EPG_XMLTV", Nothing) Then _epgXMLTV = root.GetProperty("EPG_XMLTV").GetString()
            If root.TryGetProperty("EPG_USER", Nothing) Then _epgUser = root.GetProperty("EPG_USER").GetString()
            If root.TryGetProperty("EPG_PASS", Nothing) Then _epgPass = root.GetProperty("EPG_PASS").GetString()
            If root.TryGetProperty("USER_AGENT", Nothing) Then _userAgent = root.GetProperty("USER_AGENT").GetString()
            If root.TryGetProperty("PLEX_MOVIES_PATH", Nothing) Then _plexMoviesPath = root.GetProperty("PLEX_MOVIES_PATH").GetString()
            If root.TryGetProperty("FFMPEG_PATH", Nothing) Then _ffmpegPath = root.GetProperty("FFMPEG_PATH").GetString()

            Return True
        Catch : Return False : End Try
    End Function

    Sub RunADB(args As String)
        Dim psi As New ProcessStartInfo(_adbExePath, "-s " & _firestickIp & ":5555 " & args)
        psi.WindowStyle = ProcessWindowStyle.Hidden
        psi.CreateNoWindow = True
        Process.Start(psi).WaitForExit()
    End Sub

    Sub DrawProgressBar(current As Integer, total As Integer, label As String)
        Dim progress As Double = current / total
        Dim completedParts As Integer = CInt(progress * 20)
        Try
            Console.SetCursorPosition(0, Console.CursorTop)
            Console.Write(label.PadRight(25) & ": [" & New String("="c, completedParts) & New String("-"c, 20 - completedParts) & "] " & Math.Round(progress * 100) & "% ")
        Catch : End Try
    End Sub
    Sub RebuildGuideDatabase(dbPath As String)

        If File.Exists(dbPath) Then
            File.Delete(dbPath)
        End If

        Using con As New SqliteConnection($"Data Source={dbPath}")
            con.Open()

            Dim sql =
    "
CREATE TABLE guide (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT,
    normalized_title TEXT,
    channel TEXT,
    start_utc DATETIME,
    end_utc DATETIME,
    xml_file TEXT
);
"

            Using cmd As New SqliteCommand(sql, con)
                cmd.ExecuteNonQuery()
            End Using

        End Using

    End Sub

    Sub CreateGuideIndexes(dbPath As String)

        Using con As New SqliteConnection($"Data Source={dbPath}")
            con.Open()

            ' Remove duplicates before creating unique index
            Dim cleanupSql =
"
DELETE FROM guide
WHERE rowid NOT IN (
    SELECT MIN(rowid)
    FROM guide
    GROUP BY channel, start_utc, normalized_title
);
"

            Using cleanup As New SqliteCommand(cleanupSql, con)
                cleanup.ExecuteNonQuery()
            End Using

            Dim sql =
"
DROP INDEX IF EXISTS idx_guide_start;

CREATE INDEX IF NOT EXISTS idx_guide_start_cover
ON guide(start_utc, channel, normalized_title);

CREATE INDEX IF NOT EXISTS idx_guide_channel_start
ON guide(channel, start_utc);

CREATE INDEX IF NOT EXISTS idx_guide_title_start
ON guide(normalized_title, start_utc);

CREATE UNIQUE INDEX IF NOT EXISTS idx_guide_unique
ON guide(channel, start_utc, normalized_title);
"
            Using cmd As New SqliteCommand(sql, con)
                cmd.ExecuteNonQuery()
            End Using

        End Using

    End Sub

    Public Class XtreamStream
        Public Property name As String
        Public Property stream_id As Integer
        Public Property category_id As String
        Public Property epg_channel_id As String
    End Class

    Private Function GuideDbIsEmpty(dbPath As String) As Boolean

        Try
            Using con As New SqliteConnection($"Data Source={dbPath}")
                con.Open()

                ' does table exist?
                Dim tableCmd As New SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type='table' AND name='guide'", con)

                If tableCmd.ExecuteScalar() Is Nothing Then
                    Return True ' table missing = empty
                End If

                ' count rows
                Dim countCmd As New SqliteCommand(
                "SELECT COUNT(*) FROM guide", con)

                Dim count = Convert.ToInt32(countCmd.ExecuteScalar())

                Return count = 0
            End Using

        Catch
            ' any error → treat as empty so we rebuild
            Return True
        End Try

    End Function
    Private Function LoadMyChannels(db As String) As HashSet(Of String)

        Dim channels As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        Using conn As New SQLiteConnection("Data Source=" & db)
            conn.Open()

            Dim cmd As New SQLiteCommand(
            "SELECT channel_id FROM channels WHERE is_movie_channel = 1 AND is_foreign = 0",
            conn)

            Using rdr = cmd.ExecuteReader()

                While rdr.Read()
                    channels.Add(rdr.GetString(0))
                End While

            End Using

        End Using

        Return channels

    End Function
    Function IsOwned(historyDb As String, title As String) As Boolean

        Using con As New SqliteConnection($"Data Source={historyDb}")
            con.Open()

            Dim cmd As New SqliteCommand(
            "SELECT 1 FROM recording_history WHERE title=@t AND owned=1 LIMIT 1", con)

            cmd.Parameters.AddWithValue("@t", title)

            Return cmd.ExecuteScalar() IsNot Nothing
        End Using

    End Function

    Public Async Function GetXtreamJson(epgUrl As String,
                                     user As String,
                                     pass As String,
                                     userAgent As String) As Task(Of String)

        Dim apiUrl =
        $"{_epgUrl}player_api.php?username={_epgUser}&password={_epgPass}&action=get_live_streams"
        'http://primestreams.tv:826/player_api.php?username=jFYSJ6UprmRRO&password=Hq0Nl2sZqRGSR9yo&action=get_live_streams

        Dim handler As New HttpClientHandler()
        handler.AutomaticDecompression =
        Net.DecompressionMethods.GZip Or Net.DecompressionMethods.Deflate

        Using client As New HttpClient(handler)

            client.DefaultRequestHeaders.Clear()
            client.DefaultRequestHeaders.Add("User-Agent", userAgent)
            client.DefaultRequestHeaders.Add("Accept", "*/*")

            Dim response = Await client.GetAsync(apiUrl)
            response.EnsureSuccessStatusCode()

            Return Await response.Content.ReadAsStringAsync()

        End Using

    End Function
    Public Sub UpdateStreamIds(epgUrl As String,
                            user As String,
                            pass As String,
                            streams As List(Of XtreamStream),
                            moviesDb As String)

        Using con As New SqliteConnection($"Data Source={moviesDb};Pooling=False;")
            con.Open()

            For Each s In streams

                If String.IsNullOrWhiteSpace(s.stream_id) _
                OrElse String.IsNullOrWhiteSpace(s.epg_channel_id) Then
                    Continue For
                End If

                Dim cmd As New SqliteCommand("
        UPDATE channels
        SET stream_id=@sid
        WHERE lower(channel_id)=lower(@epg)
        ", con)

                cmd.Parameters.AddWithValue("@sid", s.stream_id)
                cmd.Parameters.AddWithValue("@epg", s.epg_channel_id)

                cmd.ExecuteNonQuery()

            Next

        End Using
    End Sub
    Public Class ChannelInfo
        Public Property Nickname As String
        Public Property MyChannel As String
    End Class
End Module