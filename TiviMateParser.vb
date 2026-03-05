Imports System.IO
Imports System.Text
Imports Microsoft.Data.Sqlite

Public Class TiviMateParser

    Public Shared Function LoadBackup(path As String) As List(Of TiviRecording)

        If Not File.Exists(path) Then
            Throw New Exception("Backup file not found: " & path)
        End If

        Dim bytes = File.ReadAllBytes(path)
        Dim marker = Encoding.ASCII.GetBytes("SQLite format 3")

        Dim start = FindBytes(bytes, marker)
        If start = -1 Then
            Throw New Exception("SQLite DB not found inside backup file")
        End If

        ' Extract DB portion
        Dim dbBytes(bytes.Length - start - 1) As Byte
        Array.Copy(bytes, start, dbBytes, 0, dbBytes.Length)

        Dim tempDb = IO.Path.Combine(IO.Path.GetTempPath(), "tivimate_temp.db")
        File.WriteAllBytes(tempDb, dbBytes)

        Dim results As New List(Of TiviRecording)

        Using con As New SqliteConnection("Data Source=" & tempDb & ";Version=3;")

            con.Open()

            ' get table names
            Dim tables As New List(Of String)

            Using cmd As New SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table'", con)
                Using r = cmd.ExecuteReader()
                    While r.Read()
                        tables.Add(r.GetString(0))
                    End While
                End Using
            End Using

            ' scan tables that might contain recordings/timers
            For Each table In tables

                Dim lower = table.ToLower()

                If lower.Contains("record") OrElse
                   lower.Contains("timer") OrElse
                   lower.Contains("sched") Then

                    Using cmd As New SQLiteCommand("SELECT * FROM [" & table & "]", con)
                        Using r = cmd.ExecuteReader()

                            While r.Read()

                                Dim rec As New TiviRecording
                                rec.SourceTable = table

                                For i = 0 To r.FieldCount - 1
                                    Dim col = r.GetName(i).ToLower()

                                    If col.Contains("title") Then
                                        rec.Title = SafeString(r(i))
                                    End If

                                    If col.Contains("channel") Then
                                        rec.Channel = SafeString(r(i))
                                    End If

                                    If col.Contains("start") Then
                                        rec.StartTime = SafeDate(r(i))
                                    End If

                                    If col.Contains("end") OrElse col.Contains("stop") Then
                                        rec.EndTime = SafeDate(r(i))
                                    End If
                                Next

                                results.Add(rec)

                            End While
                        End Using
                    End Using
                End If
            Next
        End Using

        Try : File.Delete(tempDb) : Catch : End Try

        Return results
    End Function


    Private Shared Function SafeString(o As Object) As String
        If o Is Nothing OrElse IsDBNull(o) Then Return ""
        Return o.ToString()
    End Function

    Private Shared Function SafeDate(o As Object) As DateTime
        If o Is Nothing OrElse IsDBNull(o) Then Return DateTime.MinValue

        Dim s = o.ToString()

        If IsNumeric(s) Then
            Dim unix As Long = CLng(s)
            Return DateTimeOffset.FromUnixTimeSeconds(unix).DateTime
        End If

        Dim dt As DateTime
        If DateTime.TryParse(s, dt) Then Return dt

        Return DateTime.MinValue
    End Function


    Private Shared Function FindBytes(data As Byte(), pattern As Byte()) As Integer
        For i = 0 To data.Length - pattern.Length
            Dim match = True
            For j = 0 To pattern.Length - 1
                If data(i + j) <> pattern(j) Then
                    match = False
                    Exit For
                End If
            Next
            If match Then Return i
        Next
        Return -1
    End Function

End Class
