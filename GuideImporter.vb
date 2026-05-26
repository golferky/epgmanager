Imports Microsoft.Data.Sqlite
Imports System.Xml.Linq
Imports System.Globalization
Imports System.Text.RegularExpressions
Imports System.IO
Imports System.Xml
Public Module GuideImporter

    Private Class SdGuideInfo
        Public Property StartTime As DateTime
        Public Property ProgramType As Object
        Public Property SeasonNumber As Object
        Public Property EpisodeNumber As Object
        Public Property EpisodeTitle As Object
    End Class

    Private Class XmlGuideEntry
        Public Property Title As String
        Public Property NormalizedTitle As String
        Public Property Channel As String
        Public Property StartUtc As String
        Public Property EndUtc As String
        Public Property StartTime As DateTime
    End Class

    Private Function Norm(t As String) As String
        If String.IsNullOrWhiteSpace(t) Then Return ""
        t = t.ToLowerInvariant()
        t = Regex.Replace(t, "\(\d{4}\)", "")
        t = Regex.Replace(t, "[^\w\s]", "")
        Return Regex.Replace(t, "\s+", " ").Trim()
    End Function
    Public Sub ImportXml(xmlFile As String, dbPath As String)
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()

                Using pragma As New SqliteCommand("
                PRAGMA synchronous=OFF;
                PRAGMA journal_mode=MEMORY;
                PRAGMA temp_store=MEMORY;
            ", con)
                    pragma.ExecuteNonQuery()
                End Using

                Dim entries = LoadXmlEntries(xmlFile)
                Debug.WriteLine($"XML → parsed {entries.Count} programmes from {Path.GetFileName(xmlFile)}")

                If entries.Count = 0 Then
                    Debug.WriteLine($"XML → ImportXml complete: {Path.GetFileName(xmlFile)} | Inserted: 0 | Skipped: 0")
                    Return
                End If

                Dim minStart = entries.Min(Function(e) e.StartTime).ToString("yyyy-MM-dd HH:mm:ss")
                Dim maxStart = entries.Max(Function(e) e.StartTime).ToString("yyyy-MM-dd HH:mm:ss")

                Debug.WriteLine($"XML → loading existing guide keys for {minStart} through {maxStart}...")
                Dim existingGuideKeys As New HashSet(Of String)(StringComparer.Ordinal)
                Using keyCmd As New SqliteCommand("
                    SELECT channel, start_utc
                    FROM guide
                    WHERE start_utc BETWEEN @minStart AND @maxStart", con)
                    keyCmd.Parameters.AddWithValue("@minStart", minStart)
                    keyCmd.Parameters.AddWithValue("@maxStart", maxStart)
                    Using rdr = keyCmd.ExecuteReader()
                        While rdr.Read()
                            existingGuideKeys.Add(CStr(rdr("channel")) & vbTab & CStr(rdr("start_utc")))
                        End While
                    End Using
                End Using
                Debug.WriteLine($"XML → existing guide keys loaded: {existingGuideKeys.Count}")

                Debug.WriteLine("XML → loading SD episode lookup...")
                Dim sdLookup As New Dictionary(Of String, List(Of SdGuideInfo))(StringComparer.Ordinal)
                Using sdLoadCmd As New SqliteCommand("
                    SELECT normalized_title, start_utc, program_type, season_number, episode_number, episode_title
                    FROM guide
                    WHERE xml_file = 'schedules_direct'", con)
                    Using rdr = sdLoadCmd.ExecuteReader()
                        While rdr.Read()
                            If IsDBNull(rdr("normalized_title")) OrElse IsDBNull(rdr("start_utc")) Then Continue While

                            Dim startTime As DateTime
                            If Not DateTime.TryParse(CStr(rdr("start_utc")), startTime) Then Continue While

                            Dim normalized = CStr(rdr("normalized_title"))
                            Dim items As List(Of SdGuideInfo) = Nothing
                            If Not sdLookup.TryGetValue(normalized, items) Then
                                items = New List(Of SdGuideInfo)
                                sdLookup(normalized) = items
                            End If

                            items.Add(New SdGuideInfo With {
                                .StartTime = startTime,
                                .ProgramType = If(IsDBNull(rdr("program_type")), CObj(DBNull.Value), rdr("program_type")),
                                .SeasonNumber = If(IsDBNull(rdr("season_number")), CObj(DBNull.Value), rdr("season_number")),
                                .EpisodeNumber = If(IsDBNull(rdr("episode_number")), CObj(DBNull.Value), rdr("episode_number")),
                                .EpisodeTitle = If(IsDBNull(rdr("episode_title")), CObj(DBNull.Value), rdr("episode_title"))
                            })
                        End While
                    End Using
                End Using
                Debug.WriteLine($"XML → SD episode lookup loaded: {sdLookup.Count}")

                Using trans = con.BeginTransaction()

                    Dim sql = "
                    INSERT OR IGNORE INTO guide
                        (title, normalized_title, channel, start_utc, end_utc, xml_file,
                         program_type, season_number, episode_number, episode_title)
                    VALUES
                        (@t, @n, @c, @s, @e, @x, @progtype, @season, @episode, @eptitle)"

                    Using cmd As New SqliteCommand(sql, con, trans)
                        cmd.Parameters.Add("@t", SqliteType.Text)
                        cmd.Parameters.Add("@n", SqliteType.Text)
                        cmd.Parameters.Add("@c", SqliteType.Text)
                        cmd.Parameters.Add("@s", SqliteType.Text)
                        cmd.Parameters.Add("@e", SqliteType.Text)
                        cmd.Parameters.Add("@x", SqliteType.Text)
                        cmd.Parameters.Add("@progtype", SqliteType.Text)
                        cmd.Parameters.Add("@season", SqliteType.Integer)
                        cmd.Parameters.Add("@episode", SqliteType.Integer)
                        cmd.Parameters.Add("@eptitle", SqliteType.Text)

                        Dim settings As New XmlReaderSettings()
                        settings.DtdProcessing = DtdProcessing.Parse
                        Dim inserted = 0
                        Dim skipped = 0

                        For Each entry In entries
                            Dim guideKey = entry.Channel & vbTab & entry.StartUtc

                            If existingGuideKeys.Contains(guideKey) Then
                                skipped += 1
                                Continue For
                            End If

                                    ' Look up SD episode info
                                    Dim progType As Object = DBNull.Value
                                    Dim seasonNum As Object = DBNull.Value
                                    Dim episodeNum As Object = DBNull.Value
                                    Dim epTitle As Object = DBNull.Value

                                    Dim sdItems As List(Of SdGuideInfo) = Nothing
                            If sdLookup.TryGetValue(entry.NormalizedTitle, sdItems) Then
                                        Dim best As SdGuideInfo = Nothing
                                        Dim bestDelta = Double.MaxValue

                                        For Each item In sdItems
                                    Dim delta = Math.Abs((item.StartTime - entry.StartTime).TotalSeconds)
                                            If delta <= 1800 AndAlso delta < bestDelta Then
                                                best = item
                                                bestDelta = delta
                                            End If
                                        Next

                                        If best IsNot Nothing Then
                                            progType = best.ProgramType
                                            seasonNum = best.SeasonNumber
                                            episodeNum = best.EpisodeNumber
                                            epTitle = best.EpisodeTitle
                                        End If
                                    End If

                            cmd.Parameters("@t").Value = entry.Title
                            cmd.Parameters("@n").Value = entry.NormalizedTitle
                            cmd.Parameters("@c").Value = entry.Channel
                            cmd.Parameters("@s").Value = entry.StartUtc
                            cmd.Parameters("@e").Value = entry.EndUtc
                                    cmd.Parameters("@x").Value = Path.GetFileName(xmlFile)
                                    cmd.Parameters("@progtype").Value = progType
                                    cmd.Parameters("@season").Value = seasonNum
                                    cmd.Parameters("@episode").Value = episodeNum
                                    cmd.Parameters("@eptitle").Value = epTitle

                                    If cmd.ExecuteNonQuery() > 0 Then
                                        inserted += 1
                                        existingGuideKeys.Add(guideKey)
                                    Else
                                        skipped += 1
                                    End If

                                    If inserted > 0 AndAlso inserted Mod 10000 = 0 Then
                                        Debug.WriteLine($"XML → ImportXml progress: {inserted} inserted, {skipped} skipped")
                                    End If
                        Next

                        Debug.WriteLine($"XML → ImportXml complete: {Path.GetFileName(xmlFile)} | Inserted: {inserted} | Skipped: {skipped}")
                    End Using

                    trans.Commit()
                End Using
            End Using
        End SyncLock
    End Sub

    Private Function LoadXmlEntries(xmlFile As String) As List(Of XmlGuideEntry)
        Dim entries As New List(Of XmlGuideEntry)
        Dim settings As New XmlReaderSettings()
        settings.DtdProcessing = DtdProcessing.Parse

        Using reader = XmlReader.Create(xmlFile, settings)
            While reader.Read()
                If reader.NodeType <> XmlNodeType.Element OrElse reader.Name <> "programme" Then Continue While

                Dim channel = reader.GetAttribute("channel")
                Dim startAttr = reader.GetAttribute("start")
                Dim stopAttr = reader.GetAttribute("stop")
                If channel Is Nothing OrElse startAttr Is Nothing OrElse stopAttr Is Nothing Then Continue While

                Dim startUtc = ParseXmltvTime(startAttr.Substring(0, 14))
                Dim endUtc = ParseXmltvTime(stopAttr.Substring(0, 14))

                Dim title As String = ""
                While reader.Read()
                    If reader.NodeType = XmlNodeType.Element AndAlso reader.Name = "title" Then
                        title = reader.ReadElementContentAsString()
                        Exit While
                    End If
                    If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Name = "programme" Then
                        Exit While
                    End If
                End While

                If String.IsNullOrWhiteSpace(title) Then Continue While

                Dim startTime As DateTime
                If Not DateTime.TryParse(startUtc, startTime) Then Continue While

                entries.Add(New XmlGuideEntry With {
                    .Title = title,
                    .NormalizedTitle = NormalizeTitle(title),
                    .Channel = channel,
                    .StartUtc = startUtc,
                    .EndUtc = endUtc,
                    .StartTime = startTime
                })
            End While
        End Using

        Return entries
    End Function

    Private Sub EnsureIndexes(con As SqliteConnection)
        SyncLock GlobalState.DbLock
            Using cmd As New SQLiteCommand("
            CREATE UNIQUE INDEX IF NOT EXISTS idx_guide_unique
            ON guide(channel, start_utc);
        ", con)

                cmd.ExecuteNonQuery()

            End Using
        End SyncLock
    End Sub
    Private Function ParseXmltvTime(xmlTime As String) As String

        ' XMLTV examples:
        ' 20260310184000
        ' 20260310184000 +0000
        ' 20260310184000 -0400

        Dim dt As DateTime

        If xmlTime.Length > 14 Then

            ' Has timezone offset
            Dim dto = DateTimeOffset.ParseExact(
            xmlTime,
            "yyyyMMddHHmmss zzz",
            CultureInfo.InvariantCulture)

            ' Convert to LOCAL time
            dt = dto.LocalDateTime

        Else

            ' No timezone → assume LOCAL already
            dt = DateTime.ParseExact(
            xmlTime,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal)

        End If

        Return dt.ToString("yyyy-MM-dd HH:mm:ss")

    End Function
End Module
