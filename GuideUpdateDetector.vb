Imports System.IO

Public Module GuideUpdateDetector

    Private Function GetGuideFileTime(folder As String) As DateTime

        Dim guidePath = Path.Combine(folder, "guide.xml")

        If Not File.Exists(guidePath) Then
            Return DateTime.MinValue
        End If

        Return File.GetLastWriteTimeUtc(guidePath)

    End Function


    Public Function GuideNeedsUpdate(xmlFolder As String,
                                     stampFile As String) As Boolean

        Dim newest = GetGuideFileTime(xmlFolder)

        If newest = DateTime.MinValue Then
            Return False ' no guide present
        End If

        If Not File.Exists(stampFile) Then
            Return True
        End If

        Dim lastRun As DateTime

        If Not DateTime.TryParse(
                File.ReadAllText(stampFile),
                Nothing,
                Globalization.DateTimeStyles.RoundtripKind,
                lastRun) Then

            Return True
        End If

        Return newest > lastRun

    End Function


    Public Sub SaveUpdateStamp(xmlFolder As String,
                               stampFile As String)

        Dim newest = GetGuideFileTime(xmlFolder)

        If newest = DateTime.MinValue Then Exit Sub

        File.WriteAllText(stampFile, newest.ToString("o"))

    End Sub

End Module