Imports System.Text.RegularExpressions
Imports System.Globalization
Imports System.Text
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

        Dim t = RemoveDiacritics(title)
        t = Regex.Replace(t, "[^\w\s]", "")

        ' remove IPTV quality tags
        t = Regex.Replace(t, "\[(HD|SD|FHD|UHD|4K)\]", "", RegexOptions.IgnoreCase)
        t = Regex.Replace(t, "\b(HD|SD|FHD|UHD|4K|1080P|720P|HDR)\b", "", RegexOptions.IgnoreCase)

        ' remove leading broadcast junk only
        t = Regex.Replace(t, "^(NEW|PREMIERE|LIVE)\s*[:\-]\s*", "", RegexOptions.IgnoreCase)

        ' remove leading country premiere markers
        t = Regex.Replace(t, "^(US|UK|CA|AU|NZ)\s+PREMIERE[:\-]\s*", "", RegexOptions.IgnoreCase)

        ' remove leading country channel prefixes
        t = Regex.Replace(t, "^(US|UK|CA|AU|NZ)\s*[\|\-]\s*", "", RegexOptions.IgnoreCase)

        ' remove years like (1993)
        t = Regex.Replace(t, "\(\d{4}\)", "")

        ' remove standalone year
        t = Regex.Replace(t, "\b\d{4}\b", "")

        ' normalize separators
        t = t.Replace(".", " ")
        t = t.Replace("_", " ")
        t = t.Replace("-", " ")

        ' remove trailing dash artifacts
        t = Regex.Replace(t, "\-\s*$", "")

        ' collapse multiple spaces
        t = Regex.Replace(t, "\s+", " ")

        Return t.Trim()

    End Function
    Private Function RemoveDiacritics(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Return ""

        Dim normalized = value.Normalize(NormalizationForm.FormD)
        Dim sb As New StringBuilder()
        For Each ch As Char In normalized
            If CharUnicodeInfo.GetUnicodeCategory(ch) <> UnicodeCategory.NonSpacingMark Then
                sb.Append(ch)
            End If
        Next
        Return sb.ToString().Normalize(NormalizationForm.FormC)
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
        SyncLock GlobalState.DbLock
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
        End SyncLock
        Return channelId

    End Function
    Function GetChannelRankFromDb(dbPath As String, channelId As String) As Integer
        SyncLock GlobalState.DbLock
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
        End SyncLock
        Return 0

    End Function
    Function GetChannelPriority(channelName As String) As Integer

        If channelName Is Nothing Then Return 0

        Dim c = channelName.ToLower()

        If c.Contains("hd") Then Return 2

        Return 1

    End Function

End Module

