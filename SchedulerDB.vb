Imports Microsoft.Data.Sqlite
Imports System.Text.RegularExpressions

Public Module SchedulerDB
    Public Sub InsertScheduled(dbPath As String,
                               title As String,
                               channel As String,
                               startTime As DateTime,
                               endTime As DateTime)

        Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
            con.Open()

            EnsureIndex(con)

            Dim cmd As New SQLiteCommand("
                INSERT OR IGNORE INTO scheduled_recordings
                (title, normalized_title, channel, start_time, end_time, status)
                VALUES (@title,@norm,@chan,@start,@end,'scheduled')", con)

            cmd.Parameters.AddWithValue("@title", title)
            cmd.Parameters.AddWithValue("@norm", NormalizeTitle(title))
            cmd.Parameters.AddWithValue("@chan", channel)
            cmd.Parameters.AddWithValue("@start", startTime)
            cmd.Parameters.AddWithValue("@end", endTime)

            cmd.ExecuteNonQuery()
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

End Module