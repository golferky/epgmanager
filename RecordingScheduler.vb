Imports Microsoft.Data.Sqlite
Imports System.Threading
Public Module RecordingScheduler

    Private _started As New HashSet(Of Integer)
    Private _running As Boolean = False

    Public Sub StartScheduler(dbPath As String)

        _running = True

        Console.WriteLine("Recording scheduler started")

        While _running

            CheckScheduledRecordings(dbPath)

            Thread.Sleep(15000)

        End While

        Console.WriteLine("Recording scheduler stopped")

    End Sub
    Public Sub StopScheduler()

        _running = False

    End Sub
    Public Sub CheckScheduledRecordings(dbPath As String)
        Dim jobs As New List(Of (Id As Integer, Title As String, Channel As String, StartTime As DateTime, EndTime As DateTime, StreamId As String, ProgramType As String, SeasonNumber As Integer, EpisodeNumber As Integer, EpisodeTitle As String))

        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={dbPath}")
                con.Open()
                Dim sql =
                "SELECT rowid, title, channel, start_time, end_time,
                        program_type, season_number, episode_number, episode_title
                 FROM scheduled_recordings
                 WHERE status='scheduled'"
                Using cmd As New SqliteCommand(sql, con)
                    Using rdr = cmd.ExecuteReader()
                        While rdr.Read()
                            Dim id = rdr.GetInt32(0)
                            If _started.Contains(id) Then Continue While
                            Dim title = rdr.GetString(1)
                            Dim channel = rdr.GetString(2)
                            Dim startTime = DateTime.Parse(rdr.GetString(3))
                            Dim endTime = DateTime.Parse(rdr.GetString(4))
                            Dim programType = If(IsDBNull(rdr("program_type")), "", rdr.GetString(5))
                            Dim seasonNumber = If(IsDBNull(rdr("season_number")), 0, rdr.GetInt32(6))
                            Dim episodeNumber = If(IsDBNull(rdr("episode_number")), 0, rdr.GetInt32(7))
                            Dim episodeTitle = If(IsDBNull(rdr("episode_title")), "", rdr.GetString(8))
                            Dim streamId = ResolveStreamId_NoLock(con, channel)
                            jobs.Add((id, title, channel, startTime, endTime, streamId,
                                  programType, seasonNumber, episodeNumber, episodeTitle))
                        End While
                    End Using
                End Using
            End Using
        End SyncLock

        For Each job In jobs
            _started.Add(job.Id)
            Console.WriteLine("Scheduled recording → " & job.Title)
            Dim capturedJob = job
            Dim t As New System.Threading.Thread(Sub()
                                                     Recorder.RecordMovie(
                capturedJob.Title,
                capturedJob.StreamId,
                capturedJob.StartTime,
                capturedJob.EndTime,
                capturedJob.ProgramType,
                capturedJob.SeasonNumber,
                capturedJob.EpisodeNumber,
                capturedJob.EpisodeTitle)
                                                 End Sub)
            t.IsBackground = True
            t.Start()
        Next
    End Sub

    Private Function ResolveStreamId_NoLock(con As SqliteConnection, channel As String) As String

        Dim sql =
    "SELECT stream_id
     FROM channels
     WHERE channel_id=@channel"

        Using cmd As New SqliteCommand(sql, con)

            cmd.Parameters.AddWithValue("@channel", channel)

            Dim result = cmd.ExecuteScalar()

            If result IsNot Nothing Then
                Return result.ToString()
            End If

        End Using

        Throw New Exception("Stream ID not found for channel " & channel)

    End Function
End Module
