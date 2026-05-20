Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports Microsoft.Data.Sqlite
Imports System.Security.Cryptography

Public Module SchedulesDirect

    Private Const SD_BASE_URL As String = "https://json.schedulesdirect.org/20141201"
    Private Const SD_LINEUP As String = "USA-DITV515-X"

    Public _sdUser As String = ""
    Public _sdPass As String = ""

    Private _sdToken As String = ""
    Private _tokenExpiry As DateTime = DateTime.MinValue

    ' =========================================================
    ' ENTRY POINT
    ' =========================================================
    Public Sub UpdateSDGuide()
        Dim sw As New System.Diagnostics.Stopwatch()
        sw.Start()

        Debug.WriteLine("SD → UpdateSDGuide called")
        Debug.WriteLine("SD → DbPath = " & _DbPath)

        If IO.File.Exists(MY_CONFIG) Then
            Dim json = IO.File.ReadAllText(MY_CONFIG)
            Using doc = JsonDocument.Parse(json)
                Dim root = doc.RootElement
                Dim userProp As JsonElement
                Dim passProp As JsonElement
                If root.TryGetProperty("SD_USER", userProp) Then _sdUser = userProp.GetString()
                If root.TryGetProperty("SD_PASS", passProp) Then _sdPass = passProp.GetString()
            End Using
        End If

        If Not Authenticate() Then
            Debug.WriteLine("SD → Auth failed")
            Logger.Log("SD Auth failed", "SchedulesDirect", "UpdateSDGuide", "ERROR")
            Return
        End If
        Debug.WriteLine($"SD → Auth done ({sw.Elapsed.TotalSeconds:F1}s)")

        Dim channels = GetLineupChannels()
        Debug.WriteLine($"SD → Channels: {channels.Count} ({sw.Elapsed.TotalSeconds:F1}s)")
        If channels Is Nothing OrElse channels.Count = 0 Then
            Logger.Log("SD no channels returned", "SchedulesDirect", "UpdateSDGuide", "WARN")
            Return
        End If

        SyncStationsToChannels(channels)

        Dim schedules = GetSchedules(channels.Keys.ToList())
        Debug.WriteLine($"SD → Schedules: {schedules.Count} ({sw.Elapsed.TotalSeconds:F1}s)")
        If schedules Is Nothing OrElse schedules.Count = 0 Then
            Logger.Log("SD no schedules returned", "SchedulesDirect", "UpdateSDGuide", "WARN")
            Return
        End If

        Dim programIds = schedules.Select(Function(s) s.ProgramId).Distinct().ToList()
        Dim programs = GetPrograms(programIds)
        Debug.WriteLine($"SD → Programs: {programs.Count} ({sw.Elapsed.TotalSeconds:F1}s)")

        InsertToGuide(schedules, programs, channels)
        Debug.WriteLine($"SD → InsertToGuide done ({sw.Elapsed.TotalSeconds:F1}s)")

        UpdateMasterTitleTypes(programs)
        Debug.WriteLine($"SD → Complete. Total time: {sw.Elapsed.TotalMinutes:F1} mins")

        Logger.Log($"SD guide update complete in {sw.Elapsed.TotalMinutes:F1} mins", "SchedulesDirect", "UpdateSDGuide")
    End Sub

    ' =========================================================
    ' AUTHENTICATE
    ' =========================================================
    Private Function Authenticate() As Boolean
        Try
            If Not String.IsNullOrEmpty(_sdToken) AndAlso DateTime.Now < _tokenExpiry Then
                Debug.WriteLine("SD → Using cached token")
                Return True
            End If

            Dim passHash = HashPassword(_sdPass)
            Dim payload = $"{{""username"":""{_sdUser}"",""password"":""{passHash}""}}"

            Using client As New HttpClient()
                client.DefaultRequestHeaders.Add("User-Agent", "EPGManager/1.0")
                Dim content = New StringContent(payload, Encoding.UTF8, "application/json")
                Dim response = client.PostAsync($"{SD_BASE_URL}/token", content).Result
                Dim body = response.Content.ReadAsStringAsync().Result
                Debug.WriteLine("SD → Auth response: " & body)

                Dim doc = JsonDocument.Parse(body)
                If doc.RootElement.GetProperty("code").GetInt32() = 0 Then
                    _sdToken = doc.RootElement.GetProperty("token").GetString()
                    _tokenExpiry = DateTime.Now.AddHours(23)
                    Return True
                Else
                    Debug.WriteLine("SD → Auth failed: " & body)
                    Return False
                End If
            End Using

        Catch ex As Exception
            Logger.Log("SD Auth error: " & ex.Message, "SchedulesDirect", "Authenticate", "ERROR")
            Return False
        End Try
    End Function

    ' =========================================================
    ' GET LINEUP CHANNELS
    ' =========================================================
    Private Function GetLineupChannels() As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)

        Try
            Using client As New HttpClient()
                client.DefaultRequestHeaders.Add("User-Agent", "EPGManager/1.0")
                client.DefaultRequestHeaders.Add("token", _sdToken)
                Dim response = client.GetAsync($"{SD_BASE_URL}/lineups/{SD_LINEUP}").Result
                Dim body = response.Content.ReadAsStringAsync().Result
                Debug.WriteLine("SD → Lineup response: " & body.Substring(0, Math.Min(300, body.Length)))
                Dim doc = JsonDocument.Parse(body)

                Dim rootProp As JsonElement
                If doc.RootElement.TryGetProperty("response", rootProp) Then
                    Debug.WriteLine("SD → Lineup error: " & rootProp.GetString())
                    Return result
                End If

                For Each mapping In doc.RootElement.GetProperty("map").EnumerateArray()
                    Dim stationId = mapping.GetProperty("stationID").GetString()
                    Dim chanProp As JsonElement
                    Dim channel = If(mapping.TryGetProperty("channel", chanProp), chanProp.GetString(), "")
                    If Not result.ContainsKey(stationId) Then
                        result.Add(stationId, channel)
                    End If
                Next
            End Using

        Catch ex As Exception
            Logger.Log("SD GetLineupChannels error: " & ex.Message, "SchedulesDirect", "GetLineupChannels", "ERROR")
        End Try

        Return result
    End Function

    ' =========================================================
    ' SYNC STATIONS TO CHANNELS TABLE
    ' =========================================================
    Private Sub SyncStationsToChannels(channels As Dictionary(Of String, String))
        Try
            Using client As New HttpClient()
                client.DefaultRequestHeaders.Add("User-Agent", "EPGManager/1.0")
                client.DefaultRequestHeaders.Add("token", _sdToken)
                Dim response = client.GetAsync($"{SD_BASE_URL}/lineups/{SD_LINEUP}").Result
                Dim body = response.Content.ReadAsStringAsync().Result
                Dim doc = JsonDocument.Parse(body)

                Dim stationNames As New Dictionary(Of String, String)
                Dim stationsProp As JsonElement
                If doc.RootElement.TryGetProperty("stations", stationsProp) Then
                    For Each station In stationsProp.EnumerateArray()
                        Dim stationId = station.GetProperty("stationID").GetString()
                        Dim nameProp As JsonElement
                        Dim name = If(station.TryGetProperty("name", nameProp), nameProp.GetString(), stationId)
                        stationNames(stationId) = name
                    Next
                End If

                SyncLock GlobalState.DbLock
                    Using con As New SqliteConnection($"Data Source={_DbPath}")
                        con.Open()
                        Using trans = con.BeginTransaction()
                            Dim cmd As New SqliteCommand("
                                INSERT OR IGNORE INTO channels
                                    (channel_id, nickname, guide_channel, type, favorite, is_movie_channel)
                                VALUES
                                    (@id, @name, @id, 'sd', 0, 0)", con)
                            cmd.Parameters.Add("@id", SqliteType.Text)
                            cmd.Parameters.Add("@name", SqliteType.Text)
                            cmd.Transaction = trans

                            For Each kvp In channels
                                Dim channelId = "sd." & kvp.Key
                                Dim name = If(stationNames.ContainsKey(kvp.Key), stationNames(kvp.Key), channelId)
                                cmd.Parameters("@id").Value = channelId
                                cmd.Parameters("@name").Value = name
                                cmd.ExecuteNonQuery()
                            Next

                            trans.Commit()
                        End Using
                    End Using
                End SyncLock
            End Using

            Debug.WriteLine("SD → Stations synced to channels table")

        Catch ex As Exception
            Logger.Log("SD SyncStations error: " & ex.Message, "SchedulesDirect", "SyncStationsToChannels", "ERROR")
        End Try
    End Sub

    ' =========================================================
    ' SCHEDULE ENTRY CLASS
    ' =========================================================
    Public Class SDScheduleEntry
        Public Property StationId As String
        Public Property ProgramId As String
        Public Property AirDateTime As DateTime
        Public Property Duration As Integer
        Public Property IsLive As Boolean
        Public Property IsNew As Boolean
    End Class

    ' =========================================================
    ' GET SCHEDULES
    ' =========================================================
    Private Function GetSchedules(stationIds As List(Of String)) As List(Of SDScheduleEntry)
        Dim result As New List(Of SDScheduleEntry)

        Try
            Dim dateList As New List(Of String)
            For d = 0 To 13
                dateList.Add("""" & DateTime.UtcNow.AddDays(d).ToString("yyyy-MM-dd") & """")
            Next
            Dim datesJson = "[" & String.Join(",", dateList) & "]"

            Dim batchSize = 5000
            Dim batches = stationIds.Select(Function(s, i) New With {s, i}) _
                                    .GroupBy(Function(x) x.i \ batchSize) _
                                    .Select(Function(g) g.Select(Function(x) x.s).ToList()) _
                                    .ToList()

            Debug.WriteLine($"SD → Schedule batches: {batches.Count}")

            Using client As New HttpClient()
                client.DefaultRequestHeaders.Add("User-Agent", "EPGManager/1.0")
                client.DefaultRequestHeaders.Add("token", _sdToken)
                client.Timeout = TimeSpan.FromMinutes(5)

                For i = 0 To batches.Count - 1
                    Dim batch = batches(i)
                    Debug.WriteLine($"SD → Schedule batch {i + 1}/{batches.Count} ({batch.Count} stations)")

                    Dim stationList As New List(Of String)
                    For Each s In batch
                        stationList.Add("{""stationID"":""" & s & """,""date"":" & datesJson & "}")
                    Next

                    Dim content = New StringContent("[" & String.Join(",", stationList) & "]", Encoding.UTF8, "application/json")
                    Dim response = client.PostAsync($"{SD_BASE_URL}/schedules", content).Result
                    Dim body = response.Content.ReadAsStringAsync().Result
                    Dim doc = JsonDocument.Parse(body)

                    For Each station In doc.RootElement.EnumerateArray()
                        Dim stationId = station.GetProperty("stationID").GetString()
                        Dim progProp As JsonElement
                        If Not station.TryGetProperty("programs", progProp) Then Continue For

                        For Each program In progProp.EnumerateArray()
                            Try
                                Dim entry As New SDScheduleEntry
                                entry.StationId = stationId
                                entry.ProgramId = program.GetProperty("programID").GetString()
                                entry.AirDateTime = DateTime.Parse(program.GetProperty("airDateTime").GetString()).ToLocalTime()
                                entry.Duration = program.GetProperty("duration").GetInt32()

                                Dim liveProp As JsonElement
                                If program.TryGetProperty("isLive", liveProp) Then
                                    entry.IsLive = liveProp.GetBoolean()
                                End If

                                result.Add(entry)
                            Catch
                            End Try
                        Next
                    Next
                Next
            End Using

        Catch ex As Exception
            Logger.Log("SD GetSchedules error: " & ex.Message, "SchedulesDirect", "GetSchedules", "ERROR")
        End Try

        Return result
    End Function

    ' =========================================================
    ' PROGRAM CLASS
    ' =========================================================
    Public Class SDProgram
        Public Property ProgramId As String
        Public Property Title As String
        Public Property Description As String
        Public Property EpisodeTitle As String
        Public Property Genres As String
        Public Property MovieYear As String
        Public Property IsMovie As Boolean
        Public Property ProgramType As String
        Public Property SeasonNumber As Integer
        Public Property EpisodeNumber As Integer
    End Class

    ' =========================================================
    ' GET PROGRAMS
    ' =========================================================
    Private Function GetPrograms(programIds As List(Of String)) As Dictionary(Of String, SDProgram)
        Dim result As New Dictionary(Of String, SDProgram)

        Try
            Dim batchSize = 5000
            Dim batches = programIds.Select(Function(s, i) New With {s, i}) _
                                    .GroupBy(Function(x) x.i \ batchSize) _
                                    .Select(Function(g) g.Select(Function(x) x.s).ToList()) _
                                    .ToList()

            Debug.WriteLine($"SD → Program batches: {batches.Count}")

            Using client As New HttpClient()
                client.DefaultRequestHeaders.Add("User-Agent", "EPGManager/1.0")
                client.DefaultRequestHeaders.Add("token", _sdToken)
                client.Timeout = TimeSpan.FromMinutes(5)

                For i = 0 To batches.Count - 1
                    Dim batch = batches(i)
                    Debug.WriteLine($"SD → Program batch {i + 1}/{batches.Count} ({batch.Count} programs)")

                    Dim requestArray = "[" & String.Join(",", batch.Select(Function(id) $"""{id}""")) & "]"
                    Dim content = New StringContent(requestArray, Encoding.UTF8, "application/json")
                    Dim response = client.PostAsync($"{SD_BASE_URL}/programs", content).Result
                    Dim body = response.Content.ReadAsStringAsync().Result
                    Dim doc = JsonDocument.Parse(body)

                    For Each program In doc.RootElement.EnumerateArray()
                        Try
                            Dim p As New SDProgram
                            p.ProgramId = program.GetProperty("programID").GetString()

                            ' Program type from ID prefix
                            p.ProgramType = If(p.ProgramId.Length >= 2, p.ProgramId.Substring(0, 2), "")
                            p.IsMovie = (p.ProgramType = "MV")

                            ' Title with fallbacks
                            Dim titlesProp As JsonElement
                            If program.TryGetProperty("titles", titlesProp) Then
                                Dim titleKeys = {"title120", "title60", "title40", "title10"}
                                For Each t In titlesProp.EnumerateArray()
                                    For Each key In titleKeys
                                        Dim titleProp As JsonElement
                                        If t.TryGetProperty(key, titleProp) Then
                                            p.Title = titleProp.GetString()
                                            Exit For
                                        End If
                                    Next
                                    If Not String.IsNullOrEmpty(p.Title) Then Exit For
                                Next
                            End If

                            ' Episode title
                            Dim epTitleProp As JsonElement
                            If program.TryGetProperty("episodeTitle150", epTitleProp) Then
                                p.EpisodeTitle = epTitleProp.GetString()
                            End If

                            ' Description — prefer English
                            Dim descProp As JsonElement
                            If program.TryGetProperty("descriptions", descProp) Then
                                Dim desc1000 As JsonElement
                                If descProp.TryGetProperty("description1000", desc1000) Then
                                    For Each d In desc1000.EnumerateArray()
                                        Dim langProp As JsonElement
                                        Dim dProp As JsonElement
                                        If d.TryGetProperty("descriptionLanguage", langProp) AndAlso
                                           langProp.GetString() = "en" AndAlso
                                           d.TryGetProperty("description", dProp) Then
                                            p.Description = dProp.GetString()
                                            Exit For
                                        End If
                                    Next
                                    If String.IsNullOrEmpty(p.Description) Then
                                        For Each d In desc1000.EnumerateArray()
                                            Dim dProp As JsonElement
                                            If d.TryGetProperty("description", dProp) Then
                                                p.Description = dProp.GetString()
                                                Exit For
                                            End If
                                        Next
                                    End If
                                End If
                            End If

                            ' Movie year
                            Dim movieProp As JsonElement
                            If program.TryGetProperty("movie", movieProp) Then
                                Dim yearProp As JsonElement
                                If movieProp.TryGetProperty("year", yearProp) Then
                                    p.MovieYear = If(yearProp.ValueKind = JsonValueKind.Number,
                                                     yearProp.GetInt32().ToString(),
                                                     yearProp.GetString())
                                End If
                            End If
                            'If p.IsMovie Then
                            '    Debug.WriteLine($"SD MOVIE → {p.Title} | Year={p.MovieYear} | ProgramId={p.ProgramId}")
                            'End If
                            ' Genres
                            Dim genreProp As JsonElement
                            If program.TryGetProperty("genres", genreProp) Then
                                p.Genres = String.Join(",", genreProp.EnumerateArray().Select(Function(g) g.GetString()))
                            End If

                            ' Season and episode numbers
                            Dim metaProp As JsonElement
                            If program.TryGetProperty("metadata", metaProp) Then
                                For Each meta In metaProp.EnumerateArray()
                                    Dim gracenote As JsonElement
                                    If meta.TryGetProperty("Gracenote", gracenote) Then
                                        Dim seasonProp As JsonElement
                                        Dim episodeProp As JsonElement
                                        If gracenote.TryGetProperty("season", seasonProp) Then
                                            p.SeasonNumber = seasonProp.GetInt32()
                                        End If
                                        If gracenote.TryGetProperty("episode", episodeProp) Then
                                            p.EpisodeNumber = episodeProp.GetInt32()
                                        End If
                                        Exit For
                                    End If
                                Next
                            End If

                            If Not result.ContainsKey(p.ProgramId) Then
                                result.Add(p.ProgramId, p)
                            End If

                        Catch ex As Exception
                            Debug.WriteLine("SD Program parse error: " & ex.Message)
                        End Try
                    Next
                Next
            End Using

        Catch ex As Exception
            Logger.Log("SD GetPrograms error: " & ex.Message, "SchedulesDirect", "GetPrograms", "ERROR")
        End Try

        Return result
    End Function

    ' =========================================================
    ' INSERT TO GUIDE TABLE
    ' =========================================================
    Private Sub InsertToGuide(schedules As List(Of SDScheduleEntry),
                               programs As Dictionary(Of String, SDProgram),
                               channels As Dictionary(Of String, String))
        Dim inserted = 0
        Dim skipped = 0
        Dim matched = 0

        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={_DbPath}")
                con.Open()

                Dim checkCmd As New SqliteCommand("
                    SELECT 1 FROM guide
                    WHERE channel = @ch
                    AND start_utc = @st
                    LIMIT 1", con)
                checkCmd.Parameters.Add("@ch", SqliteType.Text)
                checkCmd.Parameters.Add("@st", SqliteType.Text)

                Dim lookupCmd As New SqliteCommand("
    SELECT id FROM master_titles
    WHERE normalized_title = @norm
    AND (@myear IS NULL OR CAST(year AS TEXT) = @myear)
    LIMIT 1", con)
                lookupCmd.Parameters.Add("@norm", SqliteType.Text)
                lookupCmd.Parameters.Add("@myear", SqliteType.Text)


                Dim insCmd As New SqliteCommand("
                    INSERT INTO guide
                        (title, normalized_title, channel, start_utc, end_utc, xml_file,
                         master_title_id, program_type, season_number, episode_number,
                         episode_title, year)
                    VALUES
                        (@title, @norm, @ch, @st, @et, 'schedules_direct',
                         @masterid, @progtype, @season, @episode, @eptitle, @year)", con)
                insCmd.Parameters.Add("@title", SqliteType.Text)
                insCmd.Parameters.Add("@norm", SqliteType.Text)
                insCmd.Parameters.Add("@ch", SqliteType.Text)
                insCmd.Parameters.Add("@st", SqliteType.Text)
                insCmd.Parameters.Add("@et", SqliteType.Text)
                insCmd.Parameters.Add("@masterid", SqliteType.Integer)
                insCmd.Parameters.Add("@progtype", SqliteType.Text)
                insCmd.Parameters.Add("@season", SqliteType.Integer)
                insCmd.Parameters.Add("@episode", SqliteType.Integer)
                insCmd.Parameters.Add("@eptitle", SqliteType.Text)
                insCmd.Parameters.Add("@year", SqliteType.Integer)

                Using trans = con.BeginTransaction()
                    checkCmd.Transaction = trans
                    lookupCmd.Transaction = trans
                    insCmd.Transaction = trans

                    For Each s In schedules
                        If Not programs.ContainsKey(s.ProgramId) Then
                            skipped += 1
                            Continue For
                        End If

                        Dim p = programs(s.ProgramId)
                        If String.IsNullOrEmpty(p.Title) Then
                            skipped += 1
                            Continue For
                        End If

                        Dim startStr = s.AirDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                        Dim endStr = s.AirDateTime.AddSeconds(s.Duration).ToString("yyyy-MM-dd HH:mm:ss")
                        Dim channelId = "sd." & s.StationId
                        Dim normTitle = NormalizeTitle(p.Title)

                        checkCmd.Parameters("@ch").Value = channelId
                        checkCmd.Parameters("@st").Value = startStr
                        If checkCmd.ExecuteScalar() IsNot Nothing Then
                            skipped += 1
                            Continue For
                        End If

                        lookupCmd.Parameters("@norm").Value = normTitle
                        lookupCmd.Parameters("@myear").Value = If(String.IsNullOrEmpty(p.MovieYear),
                                          CObj(DBNull.Value),
                                          CObj(p.MovieYear))
                        Dim masterIdObj = lookupCmd.ExecuteScalar()
                        Dim masterId As Object = If(masterIdObj IsNot Nothing,
                                                    CObj(CInt(masterIdObj)),
                                                    CObj(DBNull.Value))
                        If masterIdObj IsNot Nothing Then matched += 1

                        insCmd.Parameters("@title").Value = p.Title
                        insCmd.Parameters("@norm").Value = normTitle
                        insCmd.Parameters("@ch").Value = channelId
                        insCmd.Parameters("@st").Value = startStr
                        insCmd.Parameters("@et").Value = endStr
                        insCmd.Parameters("@masterid").Value = masterId
                        insCmd.Parameters("@progtype").Value = If(String.IsNullOrEmpty(p.ProgramType),
                                                                  CObj(DBNull.Value),
                                                                  CObj(p.ProgramType))
                        insCmd.Parameters("@season").Value = If(p.SeasonNumber = 0,
                                                                CObj(DBNull.Value),
                                                                CObj(p.SeasonNumber))
                        insCmd.Parameters("@episode").Value = If(p.EpisodeNumber = 0,
                                                                 CObj(DBNull.Value),
                                                                 CObj(p.EpisodeNumber))
                        insCmd.Parameters("@eptitle").Value = If(String.IsNullOrEmpty(p.EpisodeTitle),
                                                                 CObj(DBNull.Value),
                                                                 CObj(p.EpisodeTitle))
                        insCmd.Parameters("@year").Value = If(String.IsNullOrEmpty(p.MovieYear),
                                                              CObj(DBNull.Value),
                                                              CObj(CInt(p.MovieYear)))

                        insCmd.ExecuteNonQuery()
                        inserted += 1
                    Next

                    trans.Commit()
                End Using
            End Using
        End SyncLock

        Debug.WriteLine($"SD Guide → Inserted: {inserted} | Skipped: {skipped} | Matched: {matched}")
        Logger.Log($"SD inserted {inserted} guide entries ({matched} matched to master_titles)", "SchedulesDirect", "InsertToGuide")
    End Sub

    ' =========================================================
    ' UPDATE MASTER TITLE TYPES
    ' =========================================================
    Private Sub UpdateMasterTitleTypes(programs As Dictionary(Of String, SDProgram))
        Try
            Dim updated = 0
            SyncLock GlobalState.DbLock
                Using con As New SqliteConnection($"Data Source={_DbPath}")
                    con.Open()
                    Using trans = con.BeginTransaction()
                        Dim cmd As New SqliteCommand("
                            UPDATE master_titles
                            SET is_movie  = @ismovie,
                                is_series = @isseries
                            WHERE normalized_title = @norm", con)
                        cmd.Parameters.Add("@ismovie", SqliteType.Integer)
                        cmd.Parameters.Add("@isseries", SqliteType.Integer)
                        cmd.Parameters.Add("@norm", SqliteType.Text)
                        cmd.Transaction = trans

                        For Each p In programs.Values
                            If String.IsNullOrEmpty(p.Title) Then Continue For
                            cmd.Parameters("@norm").Value = NormalizeTitle(p.Title)
                            cmd.Parameters("@ismovie").Value = If(p.ProgramType = "MV", 1, 0)
                            cmd.Parameters("@isseries").Value = If(p.ProgramType = "EP", 1, 0)
                            cmd.ExecuteNonQuery()
                            updated += 1
                        Next

                        trans.Commit()
                    End Using
                End Using
            End SyncLock
            Debug.WriteLine($"SD → master_titles types updated ({updated} processed)")
        Catch ex As Exception
            Logger.Log("SD UpdateMasterTitleTypes error: " & ex.Message, "SchedulesDirect", "UpdateMasterTitleTypes", "ERROR")
        End Try
    End Sub

    ' =========================================================
    ' HELPERS
    ' =========================================================
    Private Function HashPassword(password As String) As String
        Using sha = SHA1.Create()
            Dim bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password))
            Return BitConverter.ToString(bytes).Replace("-", "").ToLower()
        End Using
    End Function

End Module