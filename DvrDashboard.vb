Imports System
Imports System.Collections.Generic
Imports System.Linq

Public Module DvrDashboard

    Public Class ActiveRecording
        Public Property Title As String
        Public Property Channel As String
        Public Property StartTime As DateTime
        Public Property EndTime As DateTime
    End Class

    Public activeRecordings As New List(Of ActiveRecording)

    Public Sub AddRecording(title As String, channel As String, endTime As DateTime)

        SyncLock activeRecordings
            activeRecordings.Add(New ActiveRecording With {
                .Title = title,
                .Channel = channel,
                .EndTime = endTime
            })
        End SyncLock

    End Sub


    Public Sub RenderDashboard()

        Console.WriteLine("")
        Console.WriteLine("ACTIVE RECORDINGS")
        Console.WriteLine("-------------------------------------------------------------")

        Console.ForegroundColor = ConsoleColor.Green

        SyncLock activeRecordings

            For Each r In activeRecordings.ToList()

                Dim remaining = r.EndTime - DateTime.Now

                If remaining.TotalSeconds <= 0 Then
                    activeRecordings.Remove(r)
                    Continue For
                End If

                Dim mins = CInt(Math.Floor(remaining.TotalMinutes))
                Dim secs = remaining.Seconds

                Console.WriteLine(
                    $"{r.Channel,-25} {r.Title,-35} {mins}:{secs:00} left")

            Next

        End SyncLock

        Console.ResetColor()

    End Sub


    Public Function ActiveCount() As Integer
        SyncLock activeRecordings
            Return activeRecordings.Count
        End SyncLock
    End Function

End Module