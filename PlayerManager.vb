Imports System.Diagnostics
Imports System.Data
Imports Microsoft.Data.Sqlite
Imports System.Net
Imports System.Runtime.InteropServices
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
        'GoTo newway
        args = {
        url,
        "--network-caching=1500",
        "--no-video-title-show",
        "--meta-title",
        windowTitle
    }
newway:
        GoTo skipnew
        ' [2026-03-17] Final VLC Launch Configuration
        args = {
    "--no-qt-name-in-title",   ' Removes "VLC Media Player" suffix
    "--no-video-title-show",   ' Disables the overlay text on the video
    "--width=800",             ' Your fixed width
    "--height=450",            ' Your fixed height
    "--no-autoscale",          ' Forces window to stay at the defined size
    "--network-caching=1500",  ' Buffer for smoother streaming
    "--meta-title",
    windowTitle,               ' Your custom window title
    url                        ' URL must remain at the end
}
skipnew:
        ' Clear any existing arguments to prevent doubling up
        psi.ArgumentList.Clear()

        For Each arg In args
            If Not String.IsNullOrEmpty(arg) Then
                psi.ArgumentList.Add(arg)
            End If
        Next

        ' Ensure the process starts correctly
        psi.UseShellExecute = False
        psi.CreateNoWindow = False

        vlcProcess = Process.Start(psi)

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
                             Threading.Thread.Sleep(200)

                             If thisProcess Is Nothing OrElse thisProcess.HasExited Then Exit Sub

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

                     Threading.Thread.Sleep(3000)

                     If thisProcess IsNot Nothing AndAlso thisProcess.HasExited Then
                         LogStreamError(channel, url, "VLC failed to open stream")
                         IncrementChannelFailure(channel)
                     End If

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

        Dim line =
        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {channel} | {url} | {message}"

        IO.File.AppendAllText(logFile, line & Environment.NewLine)

    End Sub


    Public Sub StopStream()

        Try
            If vlcProcess Is Nothing Then Exit Sub

            Dim proc = vlcProcess

            ' 🚫 If starting OR process already replaced → ignore
            If _isStartingStream Then Exit Sub
            If proc IsNot vlcProcess Then Exit Sub

            ' Already dead → cleanup
            If proc.HasExited Then
                proc.Dispose()
                If proc Is vlcProcess Then vlcProcess = Nothing
                Exit Sub
            End If

            ' ⚠️ Skip CloseMainWindow (unreliable for VLC)

            ' Force kill safely
            Try
                proc.Kill(True)
            Catch
            End Try

            proc.Dispose()

            ' Only clear if still the same instance
            If proc Is vlcProcess Then
                vlcProcess = Nothing
            End If

        Catch ex As Exception
            Debug.WriteLine("StopStream error → " & ex.Message)
        End Try

    End Sub

End Module