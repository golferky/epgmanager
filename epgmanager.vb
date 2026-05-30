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
Imports System.Linq

Public Module epgmanager
    ' --- CONFIG CONSTANTS ---
    Public Const MY_CONFIG As String = "C:\EPG\config.json"
    ' --- GLOBAL VARIABLES ---
    Public _nasIp As String = ""
    Private _nasWarehouseDir As String = ""
    Private _firestickIp As String = ""
    Private _adbExePath As String = ""
    Public _DbPath As String = ""
    Private _HistPath As String = ""
    Public _guideDir As String = ""
    Public _epgUrl As String = ""
    Public _epgXMLTV As String = ""
    Public _epgUser As String = ""
    Public _epgPass As String = ""
    Public _sdUser As String = ""
    Public _sdPass As String = ""
    Public _plexMoviesPath As String = ""
    Public _plexTvPath As String = ""
    Public _ffmpegPath As String = ""
    Public _userAgent As String = ""
    Public _rootPath As String = ""
    Public _OMDBAPIkey As String = ""
    Public _TMDBAPIkey As String = ""
    Public _recordingDir As String = ""

    Private _preferredChannels As New HashSet(Of String)
    Private ReadOnly _localDir As String = "C:\Movies\"
    Private _localDb As String = ""
    Private _localHist As String = ""

    Private shutdownRequested As Boolean = False
    Public vlcProcess As Process = Nothing
    Public _userAgentTM As String = ""

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

            If Not File.Exists(localMoviesDb) Then
                Console.WriteLine("Local DB DOES NOT EXIST")
            End If

            Console.WriteLine("Updating stream IDs...")

            Dim json = GetXtreamJson(_epgUrl, _epgUser, _epgPass, _userAgentTM)

            If json.Contains("""error""") Then
                Console.WriteLine("XTREAM API ERROR → " & json)
                Return
            End If

            Dim streams =
            Newtonsoft.Json.JsonConvert.DeserializeObject(Of List(Of XtreamStream))(json)

            UpdateStreamIds(_epgUrl, _epgUser, _epgPass, streams, localMoviesDb)

            Console.WriteLine("Stream ID update complete.")

            ' ---------------------------------------------------
            ' 2️⃣ GUIDE BUILD
            ' ---------------------------------------------------
            GuideUpdater.UpdateGuide()

            ' ---------------------------------------------------
            ' 3️⃣ SUGGESTIONS ENGINE
            ' ---------------------------------------------------
            Dim localHistoryDb = _HistPath

            Dim stats As New EngineStats

            Dim candidates =
            GuideQueryEngine.GetUpcomingCandidates(
                _DbPath,
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

            Dim step1 = scored _
            .Where(Function(x) myChannels.Contains(x.Candidate.Channel)).ToList()

            Dim step2 = step1 _
            .Where(Function(x) Not ChannelLookup.IsForeign(localMoviesDb, x.Candidate.Channel)).ToList()

            Dim step3 = step2 _
            .Where(Function(x) ChannelLookup.IsMovieChannel(localMoviesDb, x.Candidate.Channel)).ToList()

            Dim step4 = step3 _
            .Where(Function(x) x.Candidate.StartTime > DateTime.Now).ToList()

            Dim step5 = step4 _
            .GroupBy(Function(x) NormalizeTitle(x.Candidate.Title)) _
            .Select(Function(g) g _
                .OrderByDescending(Function(m) TitleHelpers.GetChannelPriority(m.Candidate.Channel)) _
                .ThenByDescending(Function(m) IsHdChannel(m.Candidate.Channel)) _
                .ThenBy(Function(m) m.Candidate.StartTime) _
                .First()).ToList()

            Dim owned As New List(Of GuideCandidate)

            For Each m In step5
                If MovieExistsInLibrary(m.Candidate.Title) Then
                    owned.Add(m.Candidate)
                End If
            Next

            Console.WriteLine()
            Console.WriteLine("===== OWNED MOVIES AIRING =====")
            Console.WriteLine()

            For Each m In owned.Take(20)
                Console.WriteLine($"{m.StartTime:HH:mm}  {m.Channel,-25}  {m.Title}")
            Next

            Dim planned = step5 _
            .Where(Function(x) Not MovieExistsInLibrary(x.Candidate.Title)) _
            .OrderBy(Function(x) x.Candidate.StartTime) _
            .Take(100)

            Console.WriteLine()
            Console.WriteLine("FILTER PIPELINE")
            Console.WriteLine("--------------------------------")
            Console.WriteLine("Scored candidates:      " & scored.Count)
            Console.WriteLine("My channels:            " & step1.Count)
            Console.WriteLine("After foreign filter:   " & step2.Count)
            Console.WriteLine("Movie channels only:    " & step3.Count)
            Console.WriteLine("Future programs:        " & step4.Count)
            Console.WriteLine("Unique titles:          " & step5.Count)
            Console.WriteLine("Final planned:          " & planned.Count)
            Console.WriteLine()

            Console.WriteLine("FIRST PLANNED RECORDINGS")
            Console.WriteLine("--------------------------------")

            For Each p In planned.Take(10)
                Console.WriteLine($"{p.Candidate.StartTime:HH:mm}  {p.Candidate.Channel,-24} {p.Candidate.Title}")
            Next

            Dim recordingLog As New List(Of String)
            Dim started As New HashSet(Of String)

            Console.CursorVisible = False

            Dim dashboardTop As Integer = Console.CursorTop
            Dim dashboardHeight As Integer = 30

            ' ---------------------------------------------------
            ' 4️⃣ MAIN SCHEDULER LOOP
            ' ---------------------------------------------------
            While True

                ' Allow graceful shutdown
                If Console.KeyAvailable Then
                    Dim key = Console.ReadKey(True)
                    If key.Key = ConsoleKey.Q Then
                        shutdownRequested = True
                        Console.WriteLine()
                        Console.WriteLine("Graceful shutdown requested...")
                    End If
                End If

                Console.SetCursorPosition(0, dashboardTop)

                WriteLineClean($"Now: {DateTime.Now:ddd MMM d HH:mm:ss}")
                WriteLineClean("")
                WriteLineClean("Press Q to shutdown safely")
                WriteLineClean("")

                WriteLineClean("NEXT RECORDINGS")
                WriteLineClean("--------------------------------------------------------------------------")
                WriteLineClean("Start   Channel                        Title                              In")
                WriteLineClean("--------------------------------------------------------------------------")

                Dim nextMovie = planned.FirstOrDefault()

                If nextMovie IsNot Nothing Then
                    Dim diff = (nextMovie.Candidate.StartTime - DateTime.Now).TotalSeconds
                    Dim mins = Math.Floor(diff / 60)
                    Dim secs = diff Mod 60
                    Dim ch = ChannelLookup.GetChannelInfo(localMoviesDb, nextMovie.Candidate.Channel)
                    WriteLineClean($"{nextMovie.Candidate.StartTime:HH:mm}   {ch.Item1,-30} {nextMovie.Candidate.Title,-35} {mins,2}:{secs:00}")
                End If

                ' ---------------------------
                ' Scheduler logic
                ' ---------------------------
                For Each s In planned

                    If shutdownRequested Then Continue For

                    Dim key =
                    s.Candidate.Channel & "|" &
                    s.Candidate.StartTime.ToString("yyyyMMddHHmm")

                    Dim diff = (s.Candidate.StartTime - DateTime.Now).TotalSeconds

                    ' Skip movies already started
                    If diff < 0 Then Continue For

                    ' Trigger recording 10 minutes before start
                    If diff <= 600 Then

                        If started.Add(key) Then

                            Dim streamId = ChannelLookup.GetStreamId(localMoviesDb, s.Candidate.Channel)

                            If String.IsNullOrWhiteSpace(streamId) Then Continue For

                            Dim ch = ChannelLookup.GetChannelInfo(localMoviesDb, s.Candidate.Channel)

                            Console.WriteLine("TRIGGERING RECORDER → " & s.Candidate.Title)

                            DvrDashboard.AddRecording(
                            s.Candidate.Title,
                            ch.Item1,
                            s.Candidate.EndTime)

                            Dim t As New Thread(Sub()
                                                    Recorder.RecordMovie(
                                s.Candidate.Title,
                                streamId,
                                s.Candidate.StartTime,
                                s.Candidate.EndTime)
                                                End Sub)
                            t.IsBackground = True
                            t.Start()

                            Dim msg =
                            $"▶ RECORDING NOW → {DateTime.Now:HH:mm:ss} | {ch.Item1} | {s.Candidate.Title}"

                            recordingLog.Add(msg)
                            Logger.Log(msg)

                        End If

                    End If

                Next

                WriteLineClean("")
                DvrDashboard.RenderDashboard()

                Dim running = Process.GetProcessesByName("ffmpeg").Length
                If shutdownRequested Then
                    WriteLineClean($"Waiting for {running} recordings to finish...")
                End If

                ' Exit once shutdown requested and no recordings active
                If shutdownRequested AndAlso running = 0 Then
                    Exit While
                End If

                For i = 1 To 50
                    If Console.KeyAvailable Then
                        Dim key = Console.ReadKey(True)
                        If key.Key = ConsoleKey.Q Then
                            shutdownRequested = True
                            Console.WriteLine()
                            Console.WriteLine("Graceful shutdown requested...")
                        End If
                    End If
                    Thread.Sleep(100)
                Next

            End While

            CleanupTempFiles()

            Console.WriteLine()
            Console.WriteLine("EPG Manager stopped safely.")

        Catch ex As Exception
            Console.WriteLine("FATAL ERROR:")
            Console.WriteLine(ex.ToString())
        End Try

    End Sub

    Private Sub CleanupTempFiles()

        Dim tmpFiles = Directory.GetFiles(_plexMoviesPath, "*.tmpmp4", SearchOption.AllDirectories)

        If tmpFiles.Length = 0 Then
            Console.WriteLine("No orphan temp files found.")
            Return
        End If

        Console.WriteLine()
        Console.WriteLine($"Found {tmpFiles.Length} unfinished recordings.")
        Console.Write("Delete them? (Y/N): ")

        Dim key = Console.ReadKey().Key
        Console.WriteLine()

        If key = ConsoleKey.Y Then

            For Each f In tmpFiles

                Try
                    File.Delete(f)
                    Console.WriteLine($"Deleted {f}")
                Catch ex As Exception
                    Console.WriteLine($"Could not delete {f}")
                End Try

            Next

        End If

    End Sub
    ' --- DOWNLOAD LOGIC (USER ARCHIVE VERSION) ---
    Public Sub DownloadGuideProperly(url As String, localPath As String)
        Try
            Dim request As Net.HttpWebRequest = Net.WebRequest.Create(url)
            request.Timeout = 300000
            request.UserAgent = _userAgent  ' ← add this, but switch to TiViMate agent

            ' Add random delay 1-10 seconds to avoid looking like a bot
            Thread.Sleep(New Random().Next(1000, 10000))

            Using response = request.GetResponse
                Using responseStream = response.GetResponseStream()
                    Using fileStream As New FileStream(localPath, FileMode.Create, FileAccess.Write)
                        responseStream.CopyTo(fileStream)
                    End Using
                End Using
            End Using

        Catch ex As Net.WebException When CType(ex.Response, Net.HttpWebResponse)?.StatusCode = Net.HttpStatusCode.Unauthorized
            ' 401 — back off and log, don't crash
            Logger.Log("Guide download blocked (401) — will retry next cycle", "GuideUpdater", "DownloadGuideProperly", "WARN")
            Thread.Sleep(TimeSpan.FromMinutes(30))  ' back off 30 mins
        Catch ex As Exception
            Logger.Log("Download failed: " & ex.Message, "GuideUpdater", "DownloadGuideProperly", "ERROR")
        End Try
    End Sub
    ' --- GUIDE PROCESSING ---
    Sub ProcessGuideXmls()
        Dim xmlFiles = Directory.GetFiles(_guideDir, "*.xml").Where(Function(f) (DateTime.Now - File.GetLastWriteTime(f)).TotalHours < 24).ToArray()
        If xmlFiles.Length = 0 Then Return
        Dim keys As New HashSet(Of String)
        SyncLock GlobalState.DbLock
            Using conn As New SqliteConnection("Data Source=" & _localHist & ";")
                conn.Open()
                Using cmd = New SqliteCommand("Select title || start || channel FROM recording_history", conn)
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
                            Using cmdIns = New SqliteCommand("INSERT INTO recording_history (title, desc, channel, start, Stop, xml_source, my_channel) VALUES (@t, @d, @c, @s, @e, @x, @m)", conn)
                                cmdIns.Parameters.AddWithValue("@t", t)
                                cmdIns.Parameters.AddWithValue("@d", node.SelectSingleNode("desc")?.InnerText)
                                cmdIns.Parameters.AddWithValue("@c", c)
                                cmdIns.Parameters.AddWithValue("@s", s)
                                cmdIns.Parameters.AddWithValue("@e", node.Attributes("Stop").Value.Substring(0, 14))
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

        End SyncLock
    End Sub

    Sub GenerateUpcomingPremiumReport(liveMap As Dictionary(Of String, String))
        Console.WriteLine(vbCrLf & "📡 SCANNING PREMIUM ENGLISH CHANNELS (Next 24h)...")
        Dim nowStr As String = DateTime.Now.ToString("yyyyMMddHHmmss")
        Dim upcoming As New List(Of Dictionary(Of String, Object))
        Dim premiums As String() = {"hbo", "sho", "max", "starz", "epix", "mgm", "tmc", "cinemax", "showtime"}
        Dim exclude As String() = {"newsmax", "shopping", "hsn", "qvc", "latino", "espanol", "paramount"}

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
                            Dim stopDt = DateTime.ParseExact(node.Attributes("Stop").Value.Substring(0, 14), "yyyyMMddHHmmss", Nothing)
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
        SyncLock GlobalState.DbLock
            Using conn As New SqliteConnection("Data Source=" & _localHist & ";")
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
        End SyncLock
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
        SyncLock GlobalState.DbLock
            Using conn As New SqliteConnection("Data Source=" & _localHist & ";")
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
        End SyncLock
    End Sub

    Sub LoadPreferredChannels()
        _preferredChannels.Clear()
        SyncLock GlobalState.DbLock
            Using conn As New SqliteConnection("Data Source=" & _localDb & ";")
                conn.Open()
                Using cmd = New SqliteCommand("SELECT channel_id FROM channels WHERE my_channel > 0", conn)
                    Using rdr = cmd.ExecuteReader()
                        While rdr.Read()
                            _preferredChannels.Add(rdr(0).ToString())
                        End While
                    End Using
                End Using
            End Using
        End SyncLock
    End Sub

    Sub EvaluateOwnedMovies()
        Console.WriteLine(vbCrLf & "[MDB] Syncing Master Database...")
        SyncLock GlobalState.DbLock
            Using conn As New SqliteConnection("Data Source=" & _localHist & ";")
                conn.Open()
                Using cmdAttach = New SqliteCommand("ATTACH DATABASE '" & _localDb & "' AS mdb", conn) : cmdAttach.ExecuteNonQuery() : End Using
                Dim count = New SqliteCommand("UPDATE recording_history SET owned = 1 WHERE UPPER(title) IN (SELECT UPPER(title) FROM mdb.master_titles)", conn).ExecuteNonQuery()
                Console.WriteLine("      Updated " & count & " records.")
                Using cmdDetach = New SqliteCommand("DETACH DATABASE mdb", conn) : cmdDetach.ExecuteNonQuery() : End Using
            End Using
        End SyncLock
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
        SyncLock GlobalState.DbLock
            Using conn As New SqliteConnection("Data Source=" & _localHist & ";")
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
        End SyncLock
    End Sub
    Function LoadConfig() As Boolean

        Try

            If Not File.Exists(MY_CONFIG) Then
                Console.WriteLine("Config not found: " & MY_CONFIG)
                MsgBox("Config not found: " & MY_CONFIG)
                Return False
            End If
            Try
                Dim root =
                    JsonDocument.Parse(File.ReadAllText(MY_CONFIG)).RootElement
                Dim prop As JsonElement
                If root.TryGetProperty("RECORDINGS_DIR", prop) Then
                    _recordingDir = prop.GetString()
                Else
                    Throw New Exception("Missing JSON key → RECORDINGS_DIR")
                End If

                _firestickIp = root.GetProperty("FIRESTICK_IP").GetString()
                _OMDBAPIkey = root.GetProperty("OMDB_KEY").GetString()
                If root.TryGetProperty("TMDB_KEY", prop) Then _TMDBAPIkey = prop.GetString()
                _DbPath = root.GetProperty("DB_PATH").GetString()
                _guideDir = root.GetProperty("GUIDE_DATA_DIR").GetString()
                _nasWarehouseDir = root.GetProperty("WAREHOUSE").GetString()
                _nasIp = root.GetProperty("MY_NAS_IP").GetString()
                _rootPath = root.GetProperty("WINDOWS_ROOT").GetString()
                _plexMoviesPath = root.GetProperty("MY_NAS_IP").GetString() & "" & root.GetProperty("PLEX_MOVIES_ROOT").GetString()
                _plexTvPath = root.GetProperty("MY_NAS_IP").GetString() & "" & root.GetProperty("PLEX_TV_ROOT").GetString()

                If root.TryGetProperty("EPG_BASE_URL", Nothing) Then _epgUrl = root.GetProperty("EPG_BASE_URL").GetString()
                If root.TryGetProperty("EPG_XMLTV", Nothing) Then _epgXMLTV = root.GetProperty("EPG_XMLTV").GetString()
                If root.TryGetProperty("EPG_USER", Nothing) Then _epgUser = root.GetProperty("EPG_USER").GetString()
                If root.TryGetProperty("EPG_PASS", Nothing) Then _epgPass = root.GetProperty("EPG_PASS").GetString()
                If root.TryGetProperty("USER_AGENT", Nothing) Then _userAgent = root.GetProperty("USER_AGENT").GetString()
                If root.TryGetProperty("USER_AGENT_TM", Nothing) Then _userAgentTM = root.GetProperty("USER_AGENT_TM").GetString()
                If root.TryGetProperty("SD_USER", Nothing) Then _sdUser = root.GetProperty("SD_USER").GetString()
                If root.TryGetProperty("SD_PASS", Nothing) Then _sdPass = root.GetProperty("SD_PASS").GetString()

                _plexMoviesPath = root.GetProperty("MY_NAS_IP").GetString() & "" & root.GetProperty("PLEX_MOVIES_ROOT").GetString()

                Dim rootDir As String

                If RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then
                    rootDir = root.GetProperty("WINDOWS_ROOT").GetString()
                    _adbExePath = root.GetProperty("ADB_WIN_PATH").GetString()
                    _ffmpegPath = root.GetProperty("FFMPEG_WINDOWS").GetString()
                Else
                    rootDir = root.GetProperty("MAC_ROOT").GetString()
                    _adbExePath = root.GetProperty("ADB_MAC_PATH").GetString()
                    _ffmpegPath = root.GetProperty("FFMPEG_MAC").GetString()
                End If
                ' Mac settings
                GlobalState.MacHost = root.GetProperty("MAC_HOST").GetString()
                GlobalState.MacUser = root.GetProperty("MAC_USER").GetString()
                GlobalState.MacPort = root.GetProperty("MAC_PORT").GetInt32()

                Dim target = root.GetProperty("EXECUTION_TARGET").GetString()
                GlobalState.CurrentTarget = If(
    target = "RemoteMac",
    ExecutionTarget.RemoteMac,
    ExecutionTarget.LocalWindows)
                SyncRecordingSettingsToDb()
                Return True

            Catch ex As Exception
                MsgBox("Error parsing config.json: " & ex.Message)
                Return False
            End Try

        Catch : Return False : End Try
    End Function

    Private Sub SyncRecordingSettingsToDb()
        If String.IsNullOrWhiteSpace(_DbPath) Then Return
        If String.IsNullOrWhiteSpace(_epgUrl) OrElse
           String.IsNullOrWhiteSpace(_epgUser) OrElse
           String.IsNullOrWhiteSpace(_epgPass) Then Return

        Try
            SyncLock GlobalState.DbLock
                Using con As New SqliteConnection($"Data Source={_DbPath};Pooling=False;")
                    con.Open()
                    Using createCmd As New SqliteCommand("
                        CREATE TABLE IF NOT EXISTS app_settings (
                            key TEXT PRIMARY KEY,
                            value TEXT NOT NULL
                        )", con)
                        createCmd.ExecuteNonQuery()
                    End Using

                    Using upsertCmd As New SqliteCommand("
                        INSERT INTO app_settings (key, value)
                        VALUES (@key, @value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value", con)
                        upsertCmd.Parameters.Add("@key", SqliteType.Text)
                        upsertCmd.Parameters.Add("@value", SqliteType.Text)

                        upsertCmd.Parameters("@key").Value = "recording_base_url"
                        upsertCmd.Parameters("@value").Value = _epgUrl
                        upsertCmd.ExecuteNonQuery()

                        upsertCmd.Parameters("@key").Value = "recording_user"
                        upsertCmd.Parameters("@value").Value = _epgUser
                        upsertCmd.ExecuteNonQuery()

                        upsertCmd.Parameters("@key").Value = "recording_pass"
                        upsertCmd.Parameters("@value").Value = _epgPass
                        upsertCmd.ExecuteNonQuery()
                    End Using
                End Using
            End SyncLock
        Catch ex As Exception
            Debug.WriteLine("SyncRecordingSettingsToDb failed: " & ex.Message)
        End Try
    End Sub

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
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()

                Using cmd As New SqliteCommand("PRAGMA busy_timeout=10000", con)
                    cmd.ExecuteNonQuery()
                End Using

                ' Create table if it doesn't exist (won't touch existing data)
                Dim createSql = "
                CREATE TABLE IF NOT EXISTS guide (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT,
                    normalized_title TEXT,
                    channel TEXT,
                    start_utc DATETIME,
                    end_utc DATETIME,
                    xml_file TEXT,
                    master_title_id INTEGER REFERENCES master_titles(id)
                );"
                Using cmd As New SqliteCommand(createSql, con)
                    cmd.ExecuteNonQuery()
                End Using

                ' Add master_title_id if missing (safe on existing databases)
                Try
                    Using cmd As New SqliteCommand("ALTER TABLE guide ADD COLUMN master_title_id INTEGER REFERENCES master_titles(id)", con)
                        cmd.ExecuteNonQuery()
                    End Using
                Catch
                    ' Column already exists — ignore
                End Try

                Using cmd As New SqliteCommand("SELECT COUNT(1) FROM guide", con)
                    Dim guideRows = CLng(cmd.ExecuteScalar())
                    Debug.WriteLine($"SD → guide rows before cleanup: {guideRows}")

                    Debug.WriteLine("SD → guide cleanup skipped during refresh")
                End Using

                Debug.WriteLine("SD → Guide database rebuilt (history preserved)")
            End Using
        End SyncLock
    End Sub

    Sub CreateGuideIndexes(dbPath As String)

        SyncLock GlobalState.DbLock

            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()

                Dim sql =
        "
DROP INDEX IF EXISTS idx_guide_start;

CREATE INDEX IF NOT EXISTS idx_guide_start_cover
ON guide(start_utc, channel, normalized_title);

CREATE INDEX IF NOT EXISTS idx_guide_channel_start
ON guide(channel, start_utc);

CREATE INDEX IF NOT EXISTS idx_guide_title_start
ON guide(normalized_title, start_utc);

CREATE INDEX IF NOT EXISTS idx_guide_unique
ON guide(channel, start_utc, normalized_title);
"
                Using cmd As New SqliteCommand(sql, con)
                    cmd.ExecuteNonQuery()
                End Using

            End Using
        End SyncLock

    End Sub

    Sub DropGuideIndexesForImport(dbPath As String)

        SyncLock GlobalState.DbLock

            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()

                Dim sql =
        "
DROP INDEX IF EXISTS idx_guide_start;
DROP INDEX IF EXISTS idx_guide_start_cover;
DROP INDEX IF EXISTS idx_guide_channel_start;
DROP INDEX IF EXISTS idx_guide_title_start;
DROP INDEX IF EXISTS idx_guide_unique;
"
                Using cmd As New SqliteCommand(sql, con)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End SyncLock

    End Sub

    Sub CreateGuideLookupIndexes(dbPath As String)

        SyncLock GlobalState.DbLock

            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()

                Dim sql =
        "
CREATE INDEX IF NOT EXISTS idx_guide_channel_start
ON guide(channel, start_utc);

CREATE INDEX IF NOT EXISTS idx_guide_xml_channel_start
ON guide(xml_file, channel, start_utc);
"
                Using cmd As New SqliteCommand(sql, con)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End SyncLock

    End Sub

    Public Class XtreamStream
        Public Property name As String
        Public Property stream_id As Integer
        Public Property category_id As String
        Public Property epg_channel_id As String
    End Class

    Public Function GuideDbIsEmpty(dbPath As String) As Boolean

        Try
            SyncLock GlobalState.DbLock
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
            End SyncLock

        Catch
            ' any error → treat as empty so we rebuild
            Return True
        End Try

    End Function
    Private Function LoadMyChannels(db As String) As HashSet(Of String)

        Dim channels As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        SyncLock GlobalState.DbLock
            Using conn As New SqliteConnection("Data Source=" & db)
                conn.Open()

                Dim cmd As New SqliteCommand(
                    "SELECT channel_id FROM channels WHERE is_movie_channel = 1 AND is_foreign = 0",
                    conn)

                Using rdr = cmd.ExecuteReader()

                    While rdr.Read()
                        channels.Add(rdr.GetString(0))
                    End While

                End Using

            End Using

        End SyncLock

        Return channels

    End Function
    Function IsOwned(historyDb As String, title As String) As Boolean
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={historyDb}")
                con.Open()

                Dim cmd As New SqliteCommand(
                    "SELECT 1 FROM recording_history WHERE title=@t AND owned=1 LIMIT 1", con)

                cmd.Parameters.AddWithValue("@t", title)

                Return cmd.ExecuteScalar() IsNot Nothing
            End Using
        End SyncLock
    End Function

    Public Function GetXtreamJson(epgUrl As String,
                             user As String,
                             pass As String,
                             userAgent As String) As String
        Try
            Dim apiUrl =
            $"{epgUrl}/player_api.php?username={user}&password={pass}&action=get_live_streams"

            Using client As New Net.WebClient()
                client.Headers.Add("User-Agent", userAgent)

                Dim json As String = client.DownloadString(apiUrl)
                Return json
            End Using

        Catch ex As Exception
            Logger.Log("Stream API error: " & ex.Message)
        End Try

        Return Nothing
    End Function
    Public Sub UpdateStreamIds(epgUrl As String,
                                user As String,
                                pass As String,
                                streams As List(Of XtreamStream),
                                moviesDb As String)
        SyncLock GlobalState.DbLock
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
        End SyncLock
    End Sub
    Private Function IsHdChannel(channel As String) As Boolean

        Dim c = channel.ToLower()

        Return c.Contains("hd") _
            OrElse c.Contains("1080") _
            OrElse c.Contains("720") _
            OrElse c.Contains("uhd") _
            OrElse c.Contains("4k")

    End Function
    Public Sub WriteLineClean(text As String)

        If text.Length < Console.WindowWidth Then
            text &= New String(" "c, Console.WindowWidth - text.Length)
        End If

        Console.WriteLine(text)

    End Sub

    Public Function MovieExistsInLibrary(title As String) As Boolean

        Dim normalized = NormalizeTitle(title)
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={_DbPath};Pooling=False;")
                con.Open()

                Dim sql = "
            SELECT 1
            FROM master_titles
            WHERE title = @t
            LIMIT 1
        "

                Using cmd As New SqliteCommand(sql, con)

                    cmd.Parameters.AddWithValue("@t", normalized)

                    Dim result = cmd.ExecuteScalar()

                    If result IsNot Nothing Then
                        Return True
                    End If

                End Using

            End Using
        End SyncLock
        Return False

    End Function

    Public Class ChannelInfo
        Public Property Nickname As String
        Public Property MyChannel As String
    End Class
End Module

