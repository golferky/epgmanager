Imports System.IO
Imports Microsoft.Data.Sqlite
Imports System.Text.Json
Public Module GuideUpdater

    Public Sub UpdateGuide()

        Dim localGuideDb = _DbPath

        Dim stampFile = Path.Combine(_guideDir, "last_import.txt")

        Dim guideUrl =
            $"{_epgUrl}{_epgXMLTV}?username={_epgUser}&password={_epgPass}"

        Dim localPath = Path.Combine(_guideDir, "guide.xml")

        Dim needsImport As Boolean =
            GuideUpdateDetector.GuideNeedsUpdate(_guideDir, stampFile) _
            OrElse GuideDbIsEmpty(localGuideDb)

        If needsImport Then

            Console.WriteLine()
            Console.WriteLine("Downloading XML guide...")

            DownloadGuideProperly(guideUrl, localPath).Wait()

            Console.WriteLine("Rebuilding guide database")

            RebuildGuideDatabase(localGuideDb)

            Console.WriteLine("Importing XML guide")

            For Each xmlFile In Directory.GetFiles(_guideDir, "*.xml")
                GuideImporter.ImportXml(xmlFile, localGuideDb)
            Next

            Console.WriteLine("Creating guide indexes")

            CreateGuideIndexes(localGuideDb)
            GuideUpdateDetector.SaveUpdateStamp(_guideDir, stampFile)
            Console.WriteLine("Refreshing provider stream IDs")
        Else
            Console.WriteLine("Guide unchanged → skipping download")
        End If
        RefreshStreamIds(localGuideDb)

    End Sub
    Public Sub RefreshStreamIds(dbPath As String)

        Dim json = GetXtreamJson(_epgUrl, _epgUser, _epgPass, _userAgent).Result
        Dim streams = JsonDocument.Parse(json).RootElement

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
                    cmd.Parameters.AddWithValue("@gid", epgId)
                    cmd.Parameters.AddWithValue("@name", name)

                    cmd.ExecuteNonQuery()

                End Using

            Next

        End Using

    End Sub
End Module