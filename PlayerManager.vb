Imports System.Diagnostics
Imports System.Data
Imports Microsoft.Data.Sqlite
Imports System.Net
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks

Public Module PlayerManager
    <DllImport("user32.dll", SetLastError:=True)>
    Private Function SetWindowPos(hWnd As IntPtr,
                                     hWndInsertAfter As IntPtr,
                                     X As Integer,
                                     Y As Integer,
                                     cx As Integer,
                                     cy As Integer,
                                     uFlags As UInteger) As Boolean
    End Function

    Private _isStartingStream As Boolean = False
    Private vlcProcess As Process = Nothing

    ' Safe helper: returns True only when a Process object is valid and still running.
    Private Function IsProcessRunning(p As Process) As Boolean
        If p Is Nothing Then
            Return False
        End If
        Try
            Return Not p.HasExited
        Catch
            ' If the Process object has no associated native process (disposed or otherwise),
            ' treat it as not running.
            Return False
        End Try
    End Function

    Public Sub PlayStream(server As String, user As String, pass As String, channel As String, streamId As String)

        _isStartingStream = True

        '---------------------------------------
        ' Stop any existing stream FIRST
        '---------------------------------------
        StopStream()

        Dim channelName = ""
        Dim title = ""
        Dim startTime As DateTime

        Dim connectionString As String = $"Data Source={_DbPath};Pooling=False;"

        Dim sql = "
SELECT c.nickname,
       g.title,
       g.start_utc
FROM channels c
JOIN guide g ON g.channel = c.channel_id
WHERE c.nickname = @channel
AND strftime('%Y%m%d%H%M%S','now','localtime') BETWEEN g.start_utc AND g.end_utc
LIMIT 1
"

        Using con As New SqliteConnection(connectionString)
            con.Open()

            Using cmd As New SqliteCommand(sql, con)
                cmd.Parameters.AddWithValue("@channel", channel)

                Using reader = cmd.ExecuteReader()
                    If reader.Read() Then
                        channelName = reader("nickname").ToString()
                        title = reader("title").ToString()

                        Dim rawDate As String = reader("start_utc").ToString()
                        startTime = DateTime.ParseExact(rawDate, "yyyyMMddHHmmss", Nothing)
                    End If
                End Using
            End Using
        End Using

        Dim url = $"{server}/live/{user}/{pass}/{streamId}.m3u8"

        '---------------------------------------
        ' Stream test
        '---------------------------------------
        Dim streamOk = TestStream(url)
        If Not streamOk Then
            LogStreamError(channel, url, "Stream test failed")
        End If

        Dim displayChannel = If(String.IsNullOrEmpty(channelName), "Live TV", channelName)
        Dim displayTitle = If(String.IsNullOrEmpty(title), "No Info", title)

        Dim windowTitle = $"{displayChannel} | {displayTitle} | {startTime:hh:mm tt} | {streamId}"

        Dim vlcPath As String = "C:\Program Files\VideoLAN\VLC\vlc.exe"

        If Not IO.File.Exists(vlcPath) Then
            MsgBox("VLC not found")
            _isStartingStream = False
            Exit Sub
        End If

        Dim psi As New ProcessStartInfo(vlcPath)
        Dim args As Object
        args = {
        url,
        "--network-caching=1500",
        "--no-video-title-show",
        "--meta-title",
        windowTitle
    }
        ' Final launch configuration (kept simple)
        psi.ArgumentList.Clear()

        For Each arg In args
            If Not String.IsNullOrEmpty(arg) Then
                psi.ArgumentList.Add(arg)
            End If
        Next

        psi.UseShellExecute = False
        psi.CreateNoWindow = False

        Try
            vlcProcess = Process.Start(psi)
        Catch ex As Exception
            Debug.WriteLine("PlayStream Process.Start error → " & ex.Message)
            vlcProcess = Nothing
        End Try

        If vlcProcess Is Nothing Then
            MsgBox("Process failed to start")
            _isStartingStream = False
            Exit Sub
        End If

        Dim thisProcess = vlcProcess

        '---------------------------------------
        ' WINDOW RESIZE (reliable)
        '---------------------------------------
        Task.Run(Sub()
                     Try
                         Dim handle As IntPtr = IntPtr.Zero

                         For i = 1 To 15
                             Thread.Sleep(200)

                             If thisProcess Is Nothing OrElse Not IsProcessRunning(thisProcess) Then Exit Sub

                             thisProcess.Refresh()
                             handle = thisProcess.MainWindowHandle

                             If handle <> IntPtr.Zero Then Exit For
                         Next

                         If handle <> IntPtr.Zero Then
                             SetWindowPos(handle, IntPtr.Zero, 100, 100, 800, 450, 0)
                         End If

                     Catch
                     End Try
                 End Sub)

        '---------------------------------------
        ' FAILURE DETECTION (safe)
        '---------------------------------------
        Task.Run(Sub()
                     Thread.Sleep(3000)
                     Try
                         If thisProcess IsNot Nothing Then
                             If Not IsProcessRunning(thisProcess) Then
                                 LogStreamError(channel, url, "VLC failed to open stream")
                                 IncrementChannelFailure(channel)
                             End If
                         End If
                     Catch ex As Exception
                         Debug.WriteLine("Failure detection error → " & ex.Message)
                     End Try
                 End Sub)

        _isStartingStream = False

    End Sub

    Private Function TestStream(url As String) As Boolean
        Try
            Dim request = CType(WebRequest.Create(url), HttpWebRequest)
            request.Method = "HEAD"
            request.Timeout = 2000
            Using response = request.GetResponse()
            End Using
            Return True
        Catch
            Return False
        End Try
    End Function

    Private Sub IncrementChannelFailure(channel As String)
        Try
            Using con As New SqliteConnection($"Data Source={_DbPath}")
                con.Open()
                Dim cmd As New SqliteCommand("
UPDATE channels
SET failed_count = failed_count + 1
WHERE nickname = @channel
", con)
                cmd.Parameters.AddWithValue("@channel", channel)
                cmd.ExecuteNonQuery()
            End Using
        Catch ex As Exception
            LogStreamError(channel, "", "Failed to update failed_count")
        End Try
    End Sub

    Private Sub LogStreamError(channel As String, url As String, message As String)
        Dim logFile = "stream_errors.log"
        Dim line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {channel} | {url} | {message}"
        IO.File.AppendAllText(logFile, line & Environment.NewLine)
    End Sub

    Public Sub StopStream()
        Try
            ' Honor starting flag first
            If _isStartingStream Then Exit Sub

            ' Atomically take the current vlcProcess reference so background tasks won't race on vlcProcess
            Dim proc As Process = Interlocked.Exchange(vlcProcess, Nothing)

            If proc Is Nothing Then Exit Sub

            Try
                If IsProcessRunning(proc) Then
                    Try
                        proc.Kill(True)
                    Catch
                        ' ignore kill exceptions
                    End Try
                End If
            Catch ex As Exception
                Debug.WriteLine("StopStream kill check error → " & ex.Message)
            Finally
                Try
                    proc.Dispose()
                Catch
                End Try
            End Try

        Catch ex As Exception
            Debug.WriteLine("StopStream error → " & ex.Message)
        End Try
    End Sub

    ' Public helpers for the UI to avoid inspecting Process objects directly
    Public Function IsVlcRunningSafe() As Boolean
        Try
            Return vlcProcess IsNot Nothing AndAlso Not vlcProcess.HasExited
        Catch
            Return False
        End Try
    End Function

    Public Async Function WaitForVlcExitSafe() As Task
        Dim p As Process = vlcProcess
        If p Is Nothing Then Return

        Try
            While True
                Try
                    If p.HasExited Then Exit While
                Catch
                    ' Process object no longer associated — exit wait
                    Exit While
                End Try

                Await Task.Delay(500)
            End While
        Catch
            ' swallow
        End Try
    End Function

End Module