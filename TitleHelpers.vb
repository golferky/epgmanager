Imports System.Text.RegularExpressions
Imports Microsoft.Data.Sqlite
Public Module TitleHelpers

    Public Function ExtractYear(title As String) As Integer?

        Dim m = Regex.Match(title, "\((19|20)\d{2}\)")

        If m.Success Then
            Return Integer.Parse(m.Value.Replace("(", "").Replace(")", ""))
        End If

        Return Nothing

    End Function


    Public Function RemoveYear(title As String) As String

        Return Regex.Replace(title, "\((19|20)\d{2}\)", "").Trim()

    End Function

    Public Function NormalizeTitle(title As String) As String

        Dim t = title

        ' remove IPTV quality tags
        t = Regex.Replace(t, "\[(HD|SD|FHD|UHD|4K)\]", "", RegexOptions.IgnoreCase)
        t = Regex.Replace(t, "\b(HD|SD|FHD|UHD|4K|1080P|720P|HDR)\b", "", RegexOptions.IgnoreCase)

        ' remove leading broadcast junk only
        t = Regex.Replace(t, "^(NEW|PREMIERE|LIVE)\s*[:\-]\s*", "", RegexOptions.IgnoreCase)

        ' remove leading country premiere markers
        t = Regex.Replace(t, "^(US|UK|CA|AU|NZ)\s+PREMIERE[:\-]\s*", "", RegexOptions.IgnoreCase)

        ' remove leading country channel prefixes
        t = Regex.Replace(t, "^(US|UK|CA|AU|NZ)\s*[\|\:\-]\s*", "", RegexOptions.IgnoreCase)

        ' remove trailing dash artifacts
        t = Regex.Replace(t, "-\s*$", "")

        ' collapse multiple spaces
        t = Regex.Replace(t, "\s+", " ")

        Return t.Trim()

    End Function
    Public Function GetChannelQuality(channel As String) As Integer

        Dim c = channel.ToLower()

        Dim score As Integer = 50

        If c.Contains("hbo") Then score = 100
        If c.Contains("cinemax") Then score = 95
        If c.Contains("showtime") Then score = 95
        If c.Contains("starz") Then score = 90
        If c.Contains("epix") Or c.Contains("mgm") Then score = 85

        If c.Contains("hd") Then score += 10
        If c.Contains("sd") Then score -= 20

        Return score

    End Function
    Public Function GetChannelName(db As String, channelId As String) As String

        Using con As New SqliteConnection($"Data Source={db}")
            con.Open()

            Dim cmd As New SQLiteCommand(
                "SELECT name FROM channels WHERE channel_id=@c", con)

            cmd.Parameters.AddWithValue("@c", channelId)

            Dim result = cmd.ExecuteScalar()

            If result IsNot Nothing Then
                Return result.ToString()
            End If
        End Using

        Return channelId

    End Function
    Function GetChannelRankFromDb(dbPath As String, channelId As String) As Integer

        Using con As New SqliteConnection($"Data Source={dbPath}")
            con.Open()

            Dim cmd As New SQLiteCommand(
            "SELECT quality_score FROM channels WHERE channel_id=@c", con)

            cmd.Parameters.AddWithValue("@c", channelId)

            Dim result = cmd.ExecuteScalar()

            If result IsNot Nothing AndAlso Not IsDBNull(result) Then
                Return Convert.ToInt32(result)
            End If

        End Using

        Return 0

    End Function
    Function GetChannelPriority(channelName As String) As Integer

        If channelName Is Nothing Then Return 0

        Dim c = channelName.ToLower()

        If c.Contains("hd") Then Return 2

        Return 1

    End Function

End Module
