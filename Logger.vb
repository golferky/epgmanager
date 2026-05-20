Imports Microsoft.Data.Sqlite
Imports System.Threading
Public Module Logger

    Public Sub Log(msg As String,
                  Optional source As String = "EPG",
                  Optional moduleName As String = "",
                  Optional methodName As String = "",
                  Optional level As String = "INFO",
                  Optional processId As Integer = 0)

        If Monitor.IsEntered(GlobalState.DbLock) Then
            Debug.WriteLine("⚠️ LOG BLOCKED: " & msg)
            Debug.WriteLine(Environment.StackTrace)   ' 🔥 ADD THIS
            Return
        End If

        Dim retries = 3

        While retries > 0
            Try
                SyncLock GlobalState.DbLock

                    Using con As New SqliteConnection($"Data Source={_DbPath};Pooling=False;")
                        con.Open()

                        Dim cmd As New SqliteCommand("
INSERT INTO logs 
(log_time, source, module, method, level, message, process_id)
VALUES 
(@time, @source, @module, @method, @level, @msg, @pid);", con)

                        cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                        cmd.Parameters.AddWithValue("@source", source)
                        cmd.Parameters.AddWithValue("@module", moduleName)
                        cmd.Parameters.AddWithValue("@method", methodName)
                        cmd.Parameters.AddWithValue("@level", level)
                        cmd.Parameters.AddWithValue("@msg", msg)
                        cmd.Parameters.AddWithValue("@pid", processId)

                        cmd.ExecuteNonQuery()
                    End Using

                End SyncLock

                Exit Sub

            Catch ex As SqliteException When ex.SqliteErrorCode = 5 ' database locked

                retries -= 1
                Threading.Thread.Sleep(100)

            Catch ex As Exception
                Debug.WriteLine("LOG FAILED: " & ex.Message)
                Exit Sub
            End Try
        End While

    End Sub
End Module