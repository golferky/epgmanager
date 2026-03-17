Imports Microsoft.Data.Sqlite
Imports System.Text.RegularExpressions

Public Module SchedulerDB
    Public Sub InsertScheduled(dbPath As String,
                               title As String,
                               channel As String,
                               startTime As DateTime,
                               endTime As DateTime,
                               processid As Integer)

        Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
            con.Open()

            EnsureIndex(con)
            Try


                Dim cmd As New SqliteCommand("
                INSERT INTO scheduled_recordings
                (title, normalized_title, channel, start_time, end_time, status, process_id)
                VALUES (@title,@norm,@chan,@start,@end,'scheduled',@processid)", con)

                cmd.Parameters.AddWithValue("@title", title)
                cmd.Parameters.AddWithValue("@norm", NormalizeTitle(title))
                cmd.Parameters.AddWithValue("@chan", channel)
                cmd.Parameters.AddWithValue("@start", startTime)
                cmd.Parameters.AddWithValue("@end", endTime)
                cmd.Parameters.AddWithValue("@processid", processid)

                cmd.ExecuteNonQuery()
            Catch ex As Exception
                MsgBox($"Movie already recording or done {title}-{channel}-{startTime}")
            End Try
        End Using

    End Sub


    Private Sub EnsureIndex(con As SqliteConnection)

        Using cmd As New SQLiteCommand("
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sched_unique
            ON scheduled_recordings(normalized_title, start_time);
        ", con)

            cmd.ExecuteNonQuery()

        End Using

    End Sub
    Public Function AlreadyScheduled(dbPath As String,
                                 title As String,
                                 startTime As DateTime) As Boolean

        Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
            con.Open()

            Dim cmd As New SqliteCommand("
            SELECT 1
            FROM scheduled_recordings
            WHERE normalized_title = @norm
            AND start_time = @start
            LIMIT 1", con)

            cmd.Parameters.AddWithValue("@norm", NormalizeTitle(title))
            cmd.Parameters.AddWithValue("@start", startTime)

            Dim result = cmd.ExecuteScalar()

            Return result IsNot Nothing

        End Using

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
            cmd.Parameters.AddWithValue("@start", startTime)

            Dim result = cmd.ExecuteScalar()

            Return result IsNot Nothing

        End Using

    End Function
    Public Function UpcomingRecordingCount(dbPath As String) As Integer

        Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
            con.Open()

            Dim cmd As New SqliteCommand("
            SELECT COUNT(*)
            FROM scheduled_recordings
            WHERE status = 'scheduled'
            AND start_time > datetime('now','localtime')", con)

            Return Convert.ToInt32(cmd.ExecuteScalar())

        End Using

    End Function

End Module