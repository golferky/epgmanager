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
    Public Function IsForeign(db As String, channel As String) As Boolean

        Using conn As New SQLiteConnection("Data Source=" & db)
            conn.Open()

            Dim cmd As New SQLiteCommand(
                "SELECT is_foreign FROM channels WHERE channel_id=@c", conn)

            cmd.Parameters.AddWithValue("@c", channel)

            Dim result = cmd.ExecuteScalar()

            If result Is Nothing Then Return False

            Return Convert.ToInt32(result) = 1

        End Using

    End Function
    Public Function IsMovieChannel(db As String, channel As String) As Boolean

        Using conn As New SQLiteConnection("Data Source=" & db)
            conn.Open()

            Dim cmd As New SQLiteCommand(
                "SELECT is_movie_channel FROM channels WHERE channel_id=@c", conn)

            cmd.Parameters.AddWithValue("@c", channel)

            Dim result = cmd.ExecuteScalar()

            If result Is Nothing Then Return False

            Return Convert.ToInt32(result) = 1

        End Using

    End Function
    Public Function GetMyChannel(db As String, channel As String) As String

        Using conn As New SQLiteConnection("Data Source=" & db)
            conn.Open()

            Dim cmd As New SQLiteCommand(
                "SELECT my_channel FROM channels WHERE channel_id=@c", conn)

            cmd.Parameters.AddWithValue("@c", channel)

            Dim result = cmd.ExecuteScalar()

            If result Is Nothing Then Return ""

            Return result.ToString()

        End Using

    End Function
    Public Function GetChannelInfo(db As String, channel As String) As (String, String)

        Using conn As New SQLiteConnection("Data Source=" & db)
            conn.Open()

            Dim cmd As New SQLiteCommand(
                "SELECT nickname, my_channel FROM channels WHERE channel_id=@c", conn)

            cmd.Parameters.AddWithValue("@c", channel)

            Using rdr = cmd.ExecuteReader()
                If rdr.Read() Then
                    Return (rdr("nickname").ToString(), rdr("my_channel").ToString())
                End If
            End Using

        End Using

        Return ("", "")

    End Function

End Module