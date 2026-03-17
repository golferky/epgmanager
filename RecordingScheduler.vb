Imports Microsoft.Data.Sqlite
Imports System.Threading
Public Module RecordingScheduler

    Private _started As New HashSet(Of Integer)
    Private _running As Boolean = False

    Public Sub StartScheduler(dbPath As String)

        _running = True

        Task.Run(Sub()

                     Console.WriteLine("Recording scheduler started")

                     While _running

                         CheckScheduledRecordings(dbPath)

                         Thread.Sleep(15000)

                     End While

                     Console.WriteLine("Recording scheduler stopped")

                 End Sub)

    End Sub
    Public Sub StopScheduler()

        _running = False

    End Sub
    Private Sub CheckScheduledRecordings(dbPath As String)

        Using con As New SqliteConnection($"Data Source={dbPath}")
            con.Open()

            Dim sql =
            "SELECT rowid,
                title,
                channel,
                start_time,
                end_time
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

                        ' Get stream_id from channels table
                        Dim streamId = ResolveStreamId(con, channel)

                        Recorder.RecordMovie(title, streamId, startTime, endTime)

                        _started.Add(id)

                        Console.WriteLine("Scheduled recording → " & title)

                    End While

                End Using

            End Using

        End Using

    End Sub
    Private Function ResolveStreamId(con As SqliteConnection, channel As String) As String

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