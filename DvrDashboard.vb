Imports System
Imports System.Collections.Generic
Imports System.Linq

Public Module DvrDashboard

    Public Class ActiveRecording
        Public Property Title As String
        Public Property Channel As String
        Public Property StartTime As DateTime
        Public Property EndTime As DateTime
        Public Property LastSize As Long
        Public Property LastCheck As DateTime
    End Class

    Public activeRecordings As New List(Of ActiveRecording)

    Public Sub AddRecording(title As String, channel As String, endTime As DateTime)

        SyncLock activeRecordings
            activeRecordings.Add(New ActiveRecording With {
                .Title = title,
                .Channel = channel,
                .StartTime = DateTime.Now,
                .EndTime = endTime
            })
        End SyncLock

    End Sub


    Public Sub RenderDashboard()
        Dim bar = ProgressBar(percent, 20)
        WriteLineClean("")
        WriteLineClean("ACTIVE RECORDINGS")
        WriteLineClean("------------------------------------------------------------------------------------------------------")
        WriteLineClean("Title                                   Start   End     Left     Size")
        WriteLineClean("------------------------------------------------------------------------------------------------------")

        Console.ForegroundColor = ConsoleColor.Green
        SyncLock activeRecordings

            For Each r In DvrDashboard.activeRecordings

                Dim remaining = r.EndTime - DateTime.Now

                If remaining.TotalSeconds < 0 Then Continue For

                Dim mins = Math.Floor(remaining.TotalMinutes)
                Dim secs = remaining.Seconds

                Dim total = (r.EndTime - r.StartTime).TotalSeconds
                Dim done = (DateTime.Now - r.StartTime).TotalSeconds

                If done < 0 Then done = 0
                If done > total Then done = total

                Dim percent = done / total
                ' Attempt to get recording file size
                Dim sizeText As String = "--"

                Try

                    Dim folder = Path.Combine(_plexMoviesPath, r.Title)
                    Dim tmpFile = Path.Combine(folder, r.Title & ".tmpmp4")

                    If File.Exists(tmpFile) Then

                        Dim fi As New FileInfo(tmpFile)

                        Dim mb = fi.Length / 1024 / 1024

                        If mb > 1024 Then
                            sizeText = $"{(mb / 1024):0.00} GB"
                        Else
                            sizeText = $"{mb:0} MB"
                        End If

                    End If

                Catch
                End Try

                WriteLineClean($"{r.Title,-40} {r.StartTime:HH:mm}  {r.EndTime:HH:mm}   {mins,2}:{secs:00}   {sizeText,8}")

            Next
        End SyncLock

        Console.ResetColor()

    End Sub


    Public Function ActiveCount() As Integer
        SyncLock activeRecordings
            Return activeRecordings.Count
        End SyncLock
    End Function
    Private Function ProgressBar(percent As Double, width As Integer) As String

        Dim filled = CInt(percent * width)

        If filled > width Then filled = width
        If filled < 0 Then filled = 0

        Return New String("█"c, filled) & New String("░"c, width - filled)

    End Function

End Module