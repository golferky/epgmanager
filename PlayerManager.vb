Imports Microsoft.Data.Sqlite
Imports System.Net
Imports System.Runtime.InteropServices
Public Module PlayerManager
    <DllImport("user32.dll", SetLastError:=True)>
    Private Function SetWindowPos(
    hWnd As IntPtr,
    hWndInsertAfter As IntPtr,
    X As Integer,
    Y As Integer,
    cx As Integer,
    cy As Integer,
    uFlags As UInteger
) As Boolean
    End Function

    Private _isStartingStream As Boolean = False
    Private vlcProcess As Process = Nothing

    Public Sub PlayStream(server As String, user As String, pass As String, channel As String, streamId As String)

        _isStartingStream = True
        Try
            If vlcProcess IsNot Nothing Then
                If vlcProcess.HasExited Then
                    vlcProcess.Dispose()
                    vlcProcess = Nothing
                End If
            End If
        Catch
            ' Process is invalid → clear it
            vlcProcess = Nothing
        End Try
        '---------------------------------------
        ' Stop any existing stream FIRST
        '---------------------------------------
        StopStream()

        Dim channelName = ""
        Dim title = ""
        Dim startTime As DateTime? = Nothing

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
            _isStartingStream = False
            Exit Sub
        End If

        Dim displayChannel = If(String.IsNullOrEmpty(channelName), "Live TV", channelName)
        Dim displayTitle = If(String.IsNullOrEmpty(title), "No Info", title)

        Dim timeText = If(startTime.HasValue, startTime.Value.ToString("hh:mm tt"), "No Time")
        Dim windowTitle = $"{displayChannel} | {displayTitle} | {timeText} | {streamId}"

        Dim vlcPath As String = "C:\Program Files\VideoLAN\VLC\vlc.exe"

        If Not IO.File.Exists(vlcPath) Then
            MsgBox("VLC not found")
            _isStartingStream = False
            Exit Sub
        End If

#Region "VLC Launch (Stable Version)"

        Dim psi As New ProcessStartInfo()

        psi.FileName = vlcPath
        psi.Arguments = """" & url & """ --meta-title=""" & windowTitle & """ --network-caching=1500"

        psi.UseShellExecute = True
        psi.CreateNoWindow = False

        Try
            Process.Start(psi)

        Catch ex As Exception
            MsgBox("ERROR: " & ex.Message)
        End Try

#End Region


        GoTo skprt
        '---------------------------------------
        ' WINDOW RESIZE (reliable)
        '---------------------------------------
        Task.Run(Sub()

                     Try
                         Dim handle As IntPtr = IntPtr.Zero

                         For i = 1 To 15
                             Threading.Thread.Sleep(200)

                             Dim p = vlcProcess
                             If p Is Nothing Then Exit Sub

                             Try
                                 If p.HasExited Then Exit Sub
                             Catch
                                 Exit Sub
                             End Try

                             Try
                                 p.Refresh()
                                 handle = p.MainWindowHandle
                             Catch
                                 Exit Sub
                             End Try

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

                     Dim failed As Boolean = True

                     Try
                         Dim p = vlcProcess

                         If p IsNot Nothing Then
                             p.Refresh()

                             If p.MainWindowHandle <> IntPtr.Zero Then
                                 failed = False
                             End If
                         End If
                     Catch
                         failed = True
                     End Try

                     If failed Then
                         LogStreamError(channel, url, "VLC failed to open stream")
                         IncrementChannelFailure(channel)
                     End If

                 End Sub)
skprt:
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
            If vlcProcess IsNot Nothing Then
                If Not vlcProcess.HasExited Then
                    vlcProcess.Kill()
                End If

                vlcProcess.Dispose()
                vlcProcess = Nothing
            End If
        Catch
            vlcProcess = Nothing
        End Try

    End Sub

End Module