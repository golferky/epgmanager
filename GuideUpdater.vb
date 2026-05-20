Imports System.IO
Imports Microsoft.Data.Sqlite
Imports System.Text.Json
Imports System.Threading

Public Module GuideUpdater

    Public Sub UpdateGuide()
        Dim localGuideDb = _DbPath
        Dim stampFile = Path.Combine(_guideDir, "last_import.txt")
        Dim guideUrl = $"{_epgUrl}{_epgXMLTV}?username={_epgUser}&password={_epgPass}"
        Dim localPath = Path.Combine(_guideDir, "guide.xml")
        Dim needsImport As Boolean =
        GuideUpdateDetector.GuideNeedsUpdate(_guideDir, stampFile) _
        OrElse GuideDbIsEmpty(localGuideDb)

        If needsImport Then
            Debug.WriteLine("")
            Debug.WriteLine("Downloading XML guide...")
            DownloadGuideProperly(guideUrl, localPath, _userAgentTM)

            If Not File.Exists(localPath) OrElse New FileInfo(localPath).Length < 1000 Then
                Debug.WriteLine("Guide download failed or empty — skipping import")
                Return
            End If

            Debug.WriteLine("Rebuilding guide database...")
            RebuildGuideDatabase(localGuideDb)

            ' SD runs FIRST so XML import can enrich from it
            Debug.WriteLine("Fetching Schedules Direct extended guide...")
            SchedulesDirect.UpdateSDGuide()

            Debug.WriteLine("Importing XML guide...")
            For Each xmlFile In Directory.GetFiles(_guideDir, "*.xml")
                GuideImporter.ImportXml(xmlFile, localGuideDb)
            Next
            Debug.WriteLine("Importing XML guide...")
            For Each xmlFile In Directory.GetFiles(_guideDir, "*.xml")
                GuideImporter.ImportXml(xmlFile, localGuideDb)
            Next

            Debug.WriteLine("Mapping SD to PS channels...")
            MapSDToPSChannels(localGuideDb)

            Debug.WriteLine("Creating guide indexes...")
            CreateGuideIndexes(localGuideDb)
            GuideUpdateDetector.SaveUpdateStamp(_guideDir, stampFile)
            Debug.WriteLine("Guide update complete.")
        Else
            Debug.WriteLine("Guide unchanged → skipping download")
        End If

        Debug.WriteLine("Refreshing provider stream IDs...")
        RefreshStreamIds(localGuideDb)
    End Sub

    Public Sub DownloadGuideProperly(url As String,
                                     localPath As String,
                                     userAgent As String)
        Try
            ' Random delay 1-10 seconds — avoids looking like a bot
            Thread.Sleep(New Random().Next(1000, 10000))

            Dim request As Net.HttpWebRequest = Net.WebRequest.Create(url)
            request.Timeout = 300000   ' 5 minutes
            request.UserAgent = userAgent

            Using response = request.GetResponse
                Using responseStream = response.GetResponseStream()
                    Using fileStream As New FileStream(
                        localPath,
                        FileMode.Create,
                        FileAccess.Write)
                        responseStream.CopyTo(fileStream)
                    End Using
                End Using
            End Using

            Console.WriteLine("Guide downloaded successfully.")

        Catch ex As Net.WebException When _
            CType(ex.Response, Net.HttpWebResponse)?.StatusCode =
            Net.HttpStatusCode.Unauthorized

            ' 401 — PrimeStreams thinks we're a bot, back off
            Logger.Log(
                "Guide download blocked (401) — backing off 30 min",
                "GuideUpdater",
                "DownloadGuideProperly",
                "WARN")

            Console.WriteLine("⚠️  Guide download blocked (401) — will retry in 30 min")
            Thread.Sleep(TimeSpan.FromMinutes(30))

        Catch ex As Net.WebException When _
            CType(ex.Response, Net.HttpWebResponse)?.StatusCode =
            Net.HttpStatusCode.TooManyRequests

            ' 429 — rate limited
            Logger.Log(
                "Guide download rate limited (429) — backing off 60 min",
                "GuideUpdater",
                "DownloadGuideProperly",
                "WARN")

            Console.WriteLine("⚠️  Guide download rate limited (429) — will retry in 60 min")
            Thread.Sleep(TimeSpan.FromMinutes(60))

        Catch ex As Exception
            Logger.Log(
                "Download failed: " & ex.Message,
                "GuideUpdater",
                "DownloadGuideProperly",
                "ERROR")
            Console.WriteLine("✗ Guide download failed: " & ex.Message)
        End Try

    End Sub
    Public Sub MapSDToPSChannels(dbPath As String)
        Try
            Debug.WriteLine("Mapping SD guide to PS channels...")
            SyncLock GlobalState.DbLock
                Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                    con.Open()
                    Using cmd As New SqliteCommand("
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
                    AND c.stream_id != 0", con)
                        Dim rows = cmd.ExecuteNonQuery()
                        Debug.WriteLine($"SD mapped → {rows} rows inserted to PS channels")
                        Logger.Log($"SD mapped {rows} rows to PS channels", "GuideUpdater", "MapSDToPSChannels")
                    End Using
                End Using
            End SyncLock
        Catch ex As Exception
            Logger.Log("MapSDToPSChannels error: " & ex.Message, "GuideUpdater", "MapSDToPSChannels", "ERROR")
        End Try
    End Sub

    Public Sub RefreshStreamIds(dbPath As String)

        Dim json = GetXtreamJson(_epgUrl, _epgUser, _epgPass, _userAgentTM)

        If String.IsNullOrWhiteSpace(json) OrElse json.Contains("""error""") Then
            Logger.Log("RefreshStreamIds — bad response from API", "GuideUpdater", "RefreshStreamIds", "WARN")
            Return
        End If

        Dim streams = JsonDocument.Parse(json).RootElement

        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={dbPath}")
                con.Open()

                For Each stream In streams.EnumerateArray()

                    Dim streamId = stream.GetProperty("stream_id").GetInt32()
                    Dim name = stream.GetProperty("name").GetString()
                    Dim epgId As String = Nothing
                    Dim prop As JsonElement

                    If stream.TryGetProperty("epg_channel_id", prop) _
                        AndAlso prop.ValueKind <> JsonValueKind.Null Then
                        epgId = prop.GetString()
                    End If

                    Dim sql As String
                    If Not String.IsNullOrEmpty(epgId) Then
                        sql = "UPDATE channels SET stream_id=@sid WHERE guide_channel=@gid"
                    Else
                        sql = "UPDATE channels SET stream_id=@sid WHERE nickname=@name"
                    End If

                    Using cmd As New SqliteCommand(sql, con)
                        cmd.Parameters.AddWithValue("@sid", streamId)
                        cmd.Parameters.AddWithValue("@gid", If(epgId, CObj(DBNull.Value)))
                        cmd.Parameters.AddWithValue("@name", name)
                        cmd.ExecuteNonQuery()
                    End Using

                Next

            End Using
        End SyncLock

        Logger.Log("Stream IDs refreshed", "GuideUpdater", "RefreshStreamIds")

    End Sub

End Module