Imports Microsoft.Data.Sqlite

Public Module GuideQueryEngine

    Public Function GetUpcomingCandidates(guideDb As String,
                                          historyDb As String,
                                          moviesDb As String,
                                          stats As EngineStats) _
                                          As List(Of GuideCandidate)

        Dim list As New List(Of GuideCandidate)

        Using guideCon As New SqliteConnection($"Data Source={guideDb};Pooling=False;"),
              histCon As New SqliteConnection($"Data Source={historyDb};Pooling=False;"),
              movCon As New SqliteConnection($"Data Source={moviesDb};Pooling=False;")

            guideCon.Open()
            histCon.Open()
            movCon.Open()

            ' ----------------------------------------
            ' LOAD OWNED TITLES INTO MEMORY
            ' ----------------------------------------
            Dim ownedSet As New HashSet(Of String)

            Using ownedCmd As New SQLiteCommand(
                "SELECT lower(title) FROM recording_history WHERE owned=1",
                histCon)

                Using r = ownedCmd.ExecuteReader()
                    While r.Read()
                        ownedSet.Add(r(0).ToString())
                    End While
                End Using
            End Using

            ' ----------------------------------------
            ' LOAD SCHEDULED INTO MEMORY
            ' ----------------------------------------
            Dim scheduledSet As New HashSet(Of String)

            Using schedCmd As New SQLiteCommand(
                "SELECT normalized_title || start_time FROM scheduled_recordings",
                movCon)

                Using r = schedCmd.ExecuteReader()
                    While r.Read()
                        scheduledSet.Add(r(0).ToString())
                    End While
                End Using
            End Using

            ' ----------------------------------------
            ' QUERY GUIDE ONCE
            ' ----------------------------------------
            Dim nowUtc = DateTime.UtcNow.ToString("yyyyMMddHHmmss")
            Dim tomorrowUtc = DateTime.UtcNow.AddHours(24).ToString("yyyyMMddHHmmss")

            Dim cmd As New SQLiteCommand("
    SELECT title, channel, start_utc, end_utc, normalized_title
    FROM guide
    WHERE start_utc BETWEEN @now AND @tomorrow
    ORDER BY start_utc", guideCon)

            cmd.Parameters.AddWithValue("@now", nowUtc)
            cmd.Parameters.AddWithValue("@tomorrow", tomorrowUtc)

            Using r = cmd.ExecuteReader()

                While r.Read()

                    stats.Programmes += 1

                    Dim title = r("title").ToString()
                    Dim channel = r("channel").ToString()

                    Dim startUtc = DateTime.Parse(r("start_utc").ToString())
                    Dim endUtc = DateTime.Parse(r("end_utc").ToString())
                    Dim norm = r("normalized_title").ToString()

                    ' -------- OWNED FILTER --------
                    If ownedSet.Contains(norm) Then
                        stats.OwnedFiltered += 1
                        Continue While
                    End If

                    ' -------- SCHEDULED FILTER --------
                    Dim schedKey = norm & startUtc.ToString("yyyy-MM-dd HH:mm:ss")

                    If scheduledSet.Contains(schedKey) Then
                        stats.ScheduledFiltered += 1
                        Continue While
                    End If

                    list.Add(New GuideCandidate With {
                        .Title = title,
                        .Channel = channel,
                        .StartTime = startUtc.ToLocalTime(),
                        .EndTime = endUtc.ToLocalTime(),
                        .HistoryState = "New"
                    })

                    stats.FinalCount += 1

                End While

            End Using

        End Using

        Return list

    End Function

End Module