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

        ' No guide file found
        If newest = DateTime.MinValue Then
            Return True
        End If

        ' Guide exists but may be empty
        Dim guideFiles = Directory.GetFiles(xmlFolder, "*.xml")

        If guideFiles.Length = 0 Then
            Return True
        End If

        For Each f In guideFiles
            Dim fi As New FileInfo(f)

            ' Empty or tiny file → force download
            If fi.Length < 1000 Then
                Return True
            End If
        Next

        ' No stamp file → first run
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

        ' Guide newer than last import
        Return newest > lastRun

    End Function

    Public Sub SaveUpdateStamp(xmlFolder As String,
                               stampFile As String)

        Dim newest = GetGuideFileTime(xmlFolder)

        If newest = DateTime.MinValue Then Exit Sub

        File.WriteAllText(stampFile, newest.ToString("o"))

    End Sub

End Module