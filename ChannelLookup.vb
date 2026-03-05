Imports Microsoft.Data.Sqlite

Public Module ChannelLookup

    Public Function GetStreamId(dbPath As String,
                                channelId As String) As String

        Using con As New SqliteConnection($"Data Source={dbPath}")
            con.Open()

            Dim cmd As New SQLiteCommand("
                SELECT stream_id
                FROM channels
                WHERE lower(channel_id)=@c
                LIMIT 1", con)

            cmd.Parameters.AddWithValue("@c", channelId.ToLower())

            Dim result = cmd.ExecuteScalar()

            If result Is Nothing Then
                Return Nothing
            End If

            Return result.ToString()

        End Using

    End Function

End Module