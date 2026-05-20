Imports Microsoft.Data.Sqlite
Imports System.Text.RegularExpressions
Imports System.Net.Http
Imports System.Text

Public Module SchedulerDB

    Public Sub InsertScheduled(dbPath As String,
                               title As String,
                               channel As String,
                               startTime As DateTime,
                               endTime As DateTime,
                               processid As Integer,
                               Optional programType As String = "",
                               Optional seasonNumber As Integer = 0,
                               Optional episodeNumber As Integer = 0,
                               Optional episodeTitle As String = "")
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()
                EnsureIndex(con)
                Try
                    Dim cmd As New SqliteCommand("
                        INSERT INTO scheduled_recordings
                            (title, normalized_title, channel, start_time, end_time, status,
                             process_id, program_type, season_number, episode_number, episode_title)
                        VALUES
                            (@title, @norm, @chan, @start, @end, 'scheduled',
                             @processid, @progtype, @season, @episode, @eptitle)", con)
                    cmd.Parameters.AddWithValue("@title", title)
                    cmd.Parameters.AddWithValue("@norm", NormalizeTitle(title))
                    cmd.Parameters.AddWithValue("@chan", channel)
                    cmd.Parameters.AddWithValue("@start", startTime.ToString("yyyy-MM-dd HH:mm:ss"))
                    cmd.Parameters.AddWithValue("@end", endTime.ToString("yyyy-MM-dd HH:mm:ss"))
                    cmd.Parameters.AddWithValue("@processid", processid)
                    cmd.Parameters.AddWithValue("@progtype", If(String.IsNullOrEmpty(programType), CObj(DBNull.Value), CObj(programType)))
                    cmd.Parameters.AddWithValue("@season", If(seasonNumber = 0, CObj(DBNull.Value), CObj(seasonNumber)))
                    cmd.Parameters.AddWithValue("@episode", If(episodeNumber = 0, CObj(DBNull.Value), CObj(episodeNumber)))
                    cmd.Parameters.AddWithValue("@eptitle", If(String.IsNullOrEmpty(episodeTitle), CObj(DBNull.Value), CObj(episodeTitle)))
                    cmd.ExecuteNonQuery()
                Catch ex As Exception
                    Logger.Log($"InsertScheduled skip: {title}-{channel}-{startTime}", "Recorder", "InsertScheduled")
                End Try
            End Using
        End SyncLock
        ' Push updated schedule to Mac
        ExportUpcomingJson(dbPath)
    End Sub

    Private Sub EnsureIndex(con As SqliteConnection)
        Using cmd As New SqliteCommand("
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sched_unique
            ON scheduled_recordings(normalized_title, start_time);
        ", con)
            cmd.ExecuteNonQuery()
        End Using
    End Sub

    Public Function AlreadyScheduled(dbPath As String,
                                     title As String,
                                     startTime As DateTime) As Boolean
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()
                Dim cmd As New SqliteCommand("
                    SELECT 1
                    FROM scheduled_recordings
                    WHERE normalized_title = @norm
                    AND start_time = @start
                    LIMIT 1", con)
                cmd.Parameters.AddWithValue("@norm", NormalizeTitle(title))
                cmd.Parameters.AddWithValue("@start", startTime.ToString("yyyy-MM-dd HH:mm:ss"))
                Dim result = cmd.ExecuteScalar()
                Return result IsNot Nothing
            End Using
        End SyncLock
    End Function

    Public Function IsScheduled(dbPath As String,
                                title As String,
                                startTime As DateTime) As Boolean
        Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
            con.Open()
            Dim cmd As New SqliteCommand("
                SELECT 1
                FROM scheduled_recordings
                WHERE normalized_title = @norm
                AND start_time = @start
                AND status IN ('scheduled','recording')
                LIMIT 1", con)
            cmd.Parameters.AddWithValue("@norm", NormalizeTitle(title))
            cmd.Parameters.AddWithValue("@start", startTime.ToString("yyyy-MM-dd HH:mm:ss"))
            Dim result = cmd.ExecuteScalar()
            Return result IsNot Nothing
        End Using
    End Function

    Public Function UpcomingRecordingCount(dbPath As String) As Integer
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
                con.Open()
                Dim cmd As New SqliteCommand("
                    SELECT COUNT(*)
                    FROM scheduled_recordings
                    WHERE status = 'scheduled'
                    AND start_time > datetime('now','localtime')", con)
                Return Convert.ToInt32(cmd.ExecuteScalar())
            End Using
        End SyncLock
    End Function

    Public Sub ExportUpcomingJson(dbPath As String)
        Try
            Dim rows As New List(Of Object)

            SyncLock GlobalState.DbLock
                Using con As New SqliteConnection($"Data Source={dbPath}")
                    con.Open()
                    Using cmd As New SqliteCommand("
                        SELECT title, channel, start_time, end_time,
                               program_type, season_number, episode_number,
                               episode_title, status
                        FROM scheduled_recordings
                        WHERE status IN ('scheduled', 'queued', 'recording')
                        ORDER BY start_time
                        LIMIT 50", con)
                        Using rdr = cmd.ExecuteReader()
                            While rdr.Read()
                                rows.Add(New With {
                                    .title = rdr("title").ToString(),
                                    .channel = rdr("channel").ToString(),
                                    .start_time = rdr("start_time").ToString(),
                                    .end_time = rdr("end_time").ToString(),
                                    .program_type = If(IsDBNull(rdr("program_type")), "", rdr("program_type").ToString()),
                                    .season_number = If(IsDBNull(rdr("season_number")), 0, Convert.ToInt32(rdr("season_number"))),
                                    .episode_number = If(IsDBNull(rdr("episode_number")), 0, Convert.ToInt32(rdr("episode_number"))),
                                    .episode_title = If(IsDBNull(rdr("episode_title")), "", rdr("episode_title").ToString()),
                                    .status = rdr("status").ToString()
                                })
                            End While
                        End Using
                    End Using
                End Using
            End SyncLock

            Dim json = System.Text.Json.JsonSerializer.Serialize(rows)

            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(5)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim macUrl = $"http://{GlobalState.MacHost}:{GlobalState.MacPort}/schedule"
                client.PostAsync(macUrl, content).Wait()
                Debug.WriteLine($"Upcoming schedule pushed to Mac → {rows.Count} recordings")
            End Using

        Catch ex As Exception
            Logger.Log("ExportUpcomingJson error: " & ex.Message, "SchedulerDB", "ExportUpcomingJson", "ERROR")
        End Try
    End Sub

End Module