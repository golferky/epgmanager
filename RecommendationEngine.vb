Imports System.Text.RegularExpressions

Public Class ScoredCandidate
    Public Property Candidate As GuideCandidate
    Public Property Score As Integer
    Public Property Reason As String
End Class

Public Module RecommendationEngine

    Public FavoriteChannels As New List(Of String) From {
        "hbo",
        "showtime",
        "amc"
    }

    Public FavoriteKeywords As New List(Of String) From {
        "premiere",
        "finale",
        "new",
        "movie"
    }

    Public Function ScoreCandidate(c As GuideCandidate) As ScoredCandidate

        Dim score As Integer = 0
        Dim reasons As New List(Of String)

        Dim titleLower = c.Title.ToLower()
        Dim channelLower = c.Channel.ToLower()

        ' -----------------------------
        ' Base Score
        ' -----------------------------
        score += 20

        ' -----------------------------
        ' Favorite Channel (partial match)
        ' -----------------------------
        For Each fav In FavoriteChannels
            If channelLower.Contains(fav) Then
                score += 40
                reasons.Add("Favorite channel")
                Exit For
            End If
        Next

        ' -----------------------------
        ' Keyword Matching
        ' -----------------------------
        For Each word In FavoriteKeywords
            If titleLower.Contains(word) Then
                score += 30
                reasons.Add("Keyword: " & word)
            End If
        Next

        ' -----------------------------
        ' Starts Soon Boost
        ' -----------------------------
        Dim minutesUntil = (c.StartTime - DateTime.Now).TotalMinutes

        If minutesUntil <= 30 AndAlso minutesUntil >= 0 Then
            score += 25
            reasons.Add("Starting soon")
        End If

        ' -----------------------------
        ' Prime Time Boost
        ' -----------------------------
        If c.StartTime.Hour >= 18 AndAlso c.StartTime.Hour <= 22 Then
            score += 15
            reasons.Add("Prime time")
        End If

        ' -----------------------------
        ' Late Night Penalty
        ' -----------------------------
        If c.StartTime.Hour < 6 Then
            score -= 10
            reasons.Add("Late night")
        End If

        Return New ScoredCandidate With {
            .Candidate = c,
            .Score = score,
            .Reason = String.Join(", ", reasons)
        }

    End Function

    Public Function ScoreAll(list As List(Of GuideCandidate)) As List(Of ScoredCandidate)

        Return list.
            Select(Function(c) ScoreCandidate(c)).
            OrderByDescending(Function(x) x.Score).
            ThenBy(Function(x) x.Candidate.StartTime).
            ToList()

    End Function

End Module