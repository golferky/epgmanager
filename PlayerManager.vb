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
        'StopStream()

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
        SyncLock GlobalState.DbLock
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
        End SyncLock

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
#Region "VLC Launch (Dual Platform)"

        If GlobalState.CurrentTarget = ExecutionTarget.RemoteMac Then
            ' ============================================================
            ' MAC → Single Instance (Maximum Stability)
            ' ============================================================
            Dim customUA As String = "VLC/3.0" ' Short & Clean
            Dim psi As New ProcessStartInfo()
            psi.FileName = "ssh"

            ' REMOVED: -n flag (we are reusing the main VLC window now)
            ' ADDED: --http-reconnect and --http-user-agent
            ' This is the most reliable "Fire and Forget" string for PrimeStreams.
            psi.Arguments = $"{GlobalState.MacUser}@{GlobalState.MacHost} open -a VLC --args --http-reconnect --http-user-agent=""{customUA}"" ""{url}"""

            psi.UseShellExecute = False
            psi.CreateNoWindow = True

            Try
                Process.Start(psi)
            Catch ex As Exception
                ' Handle SSH error
            End Try
        Else

            ' ================================
            ' WINDOWS → existing behavior
            ' ================================
            If Not IO.File.Exists(vlcPath) Then
                MsgBox("VLC not found")
                _isStartingStream = False
                Exit Sub
            End If

            Dim psi As New ProcessStartInfo()
            psi.FileName = vlcPath
            psi.Arguments = """" & url & """ --meta-title=""" & windowTitle & """ --network-caching=1500"

            psi.UseShellExecute = True
            psi.CreateNoWindow = False

            Try
                vlcProcess = Process.Start(psi)
            Catch ex As Exception
                MsgBox("ERROR: " & ex.Message)
            End Try

        End If

#End Region

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
            SyncLock GlobalState.DbLock
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
            End SyncLock
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