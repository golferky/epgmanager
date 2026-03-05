Imports Microsoft.Data.Sqlite
Imports System.Globalization
Imports System.Xml.Linq
Imports System.Text.RegularExpressions
Imports System.IO

Public Class GuideCandidate
    Public Property Title As String
    Public Property Channel As String
    Public Property StartTime As DateTime
    Public Property EndTime As DateTime
    Public Property HistoryState As String
End Class

Public Module GuideCandidateEngine

    Public Class EngineStats
        Public XmlFiles As Integer
        Public Programmes As Integer
        Public PastFiltered As Integer
        Public FutureFiltered As Integer
        Public OwnedFiltered As Integer
        Public ScheduledFiltered As Integer
        Public FinalCount As Integer
    End Class

    ' -------------------------------------------------
    ' Normalize titles consistently
    ' -------------------------------------------------
    Private Function NormalizeTitle(title As String) As String
        If String.IsNullOrWhiteSpace(title) Then Return ""
        Dim t = title.ToLowerInvariant()
        t = Regex.Replace(t, "\(\d{4}\)", "")
        t = Regex.Replace(t, "[^\w\s]", "")
        t = Regex.Replace(t, "\s+", " ").Trim()
        Return t
    End Function

    Private Function ConnStr(path As String) As String
        Return $"Data Source=""{path}"";Pooling=False;"
    End Function

    ' -------------------------------------------------
    ' MAIN ENTRY
    ' -------------------------------------------------
    Public Function GetCandidatesFromFolder(folderPath As String,
                                            historyDb As String,
                                            moviesDb As String,
                                            stats As EngineStats) _
                                            As List(Of GuideCandidate)

        Dim results As New List(Of GuideCandidate)

        If Not Directory.Exists(folderPath) Then
            Return results
        End If

        Dim files = Directory.GetFiles(folderPath, "*.xml").
                     OrderByDescending(Function(f) File.GetLastWriteTime(f)).
                     ToList()

        stats.XmlFiles = files.Count

        If files.Count = 0 Then
            Return results
        End If

        ' Local DB copies once
        Dim localHistoryDb = DbCache.GetLocalCopy(historyDb)
        Dim localMoviesDb = DbCache.GetLocalCopy(moviesDb)

        ' Load owned + scheduled sets once
        Dim ownedSet = LoadOwnedSet(localHistoryDb)
        Dim scheduledSet = LoadScheduledSet(localMoviesDb)

        Dim nowUtc = DateTime.UtcNow
        Dim cutoffUtc = nowUtc.AddHours(24)

        Dim seen As New HashSet(Of String) ' prevent duplicates

        For Each file In files

            Console.WriteLine("Parsing file: " & file)

            Dim doc As XDocument

            Try
                doc = XDocument.Load(file)
            Catch
                Continue For
            End Try

            For Each prog In doc.Descendants().
                Where(Function(x) x.Name.LocalName = "programme")

                stats.Programmes += 1

                Dim startStr = prog.Attribute("start")?.Value
                If startStr Is Nothing Then Continue For

                Dim cleanStart = startStr.Split(" "c)(0)

                Dim startUtc As DateTime

                If Not DateTime.TryParseExact(cleanStart,
                                              "yyyyMMddHHmmss",
                                              CultureInfo.InvariantCulture,
                                              DateTimeStyles.AssumeUniversal,
                                              startUtc) Then Continue For

                If startUtc < nowUtc Then
                    stats.PastFiltered += 1
                    Continue For
                End If

                If startUtc > cutoffUtc Then
                    stats.FutureFiltered += 1
                    Continue For
                End If

                Dim stopStr = prog.Attribute("stop")?.Value
                Dim endUtc As DateTime = startUtc

                If stopStr IsNot Nothing Then
                    Dim cleanStop = stopStr.Split(" "c)(0)
                    DateTime.TryParseExact(cleanStop,
                                           "yyyyMMddHHmmss",
                                           CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal,
                                           endUtc)
                End If

                Dim title = prog.Elements().
                            FirstOrDefault(Function(x) x.Name.LocalName = "title")?.
                            Value

                If String.IsNullOrWhiteSpace(title) Then Continue For

                Dim channel = prog.Attribute("channel")?.Value

                Dim norm = NormalizeTitle(title)

                ' Owned filter
                If ownedSet.Contains(norm) Then
                    stats.OwnedFiltered += 1
                    Continue For
                End If

                ' Scheduled filter
                Dim schedKey = norm & startUtc.ToString("yyyyMMddHHmmss")

                If scheduledSet.Contains(schedKey) Then
                    stats.ScheduledFiltered += 1
                    Continue For
                End If

                ' Duplicate prevention
                Dim uniqueKey = channel & startUtc.ToString("yyyyMMddHHmmss") & norm

                If seen.Contains(uniqueKey) Then Continue For
                seen.Add(uniqueKey)

                results.Add(New GuideCandidate With {
                    .Title = title,
                    .Channel = channel,
                    .StartTime = startUtc.ToLocalTime(),
                    .EndTime = endUtc.ToLocalTime(),
                    .HistoryState = "New"
                })

            Next

        Next

        stats.FinalCount = results.Count

        Return results

    End Function

    ' -------------------------------------------------
    ' Preload owned titles
    ' -------------------------------------------------
    Private Function LoadOwnedSet(historyDb As String) As HashSet(Of String)

        Dim setOwned As New HashSet(Of String)

        Using con As New SqliteConnection(ConnStr(historyDb))
            con.Open()

            Dim cmd As New SQLiteCommand(
                "SELECT normalized_title FROM recording_history WHERE owned=1", con)

            Using r = cmd.ExecuteReader()
                While r.Read()
                    If Not IsDBNull(r(0)) Then
                        setOwned.Add(r(0).ToString())
                    End If
                End While
            End Using
        End Using

        Return setOwned

    End Function

    ' -------------------------------------------------
    ' Preload scheduled recordings
    ' -------------------------------------------------
    Private Function LoadScheduledSet(moviesDb As String) As HashSet(Of String)

        Dim setScheduled As New HashSet(Of String)

        Using con As New SqliteConnection(ConnStr(moviesDb))
            con.Open()

            Dim cmd As New SQLiteCommand(
                "SELECT normalized_title, start_time FROM scheduled_recordings", con)

            Using r = cmd.ExecuteReader()
                While r.Read()
                    If Not IsDBNull(r(0)) AndAlso Not IsDBNull(r(1)) Then
                        Dim key = r(0).ToString() &
                                  Convert.ToDateTime(r(1)).
                                  ToUniversalTime().
                                  ToString("yyyyMMddHHmmss")
                        setScheduled.Add(key)
                    End If
                End While
            End Using
        End Using

        Return setScheduled

    End Function

End Module