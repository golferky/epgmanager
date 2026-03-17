Imports System.IO
Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports System.Text.RegularExpressions

Public Module Recorder

    Private Const START_PADDING_SECONDS As Integer = 60
    Private Const END_PADDING_SECONDS As Integer = 120

    Private ReadOnly _recordingLimiter As New SemaphoreSlim(10)
    Private _activeRecordings As New Dictionary(Of String, Process)

    Public LOG_FILE As String = Path.Combine(_rootPath, "logs", "recordings.log")

    ' =========================================================
    ' ENTRY POINT
    ' =========================================================
    Public Sub RecordMovie(title As String,
                           streamId As String,
                           startTime As DateTime,
                           endTime As DateTime)

        Task.Run(Async Function()

                     Await _recordingLimiter.WaitAsync()

                     Try
                         Await RunRecording(title, streamId, startTime, endTime)
                     Finally
                         _recordingLimiter.Release()
                     End Try

                 End Function)

    End Sub

    ' =========================================================
    ' MAIN PIPELINE
    ' =========================================================
    Private Async Function RunRecording(title As String,
                                        streamId As String,
                                        startTime As DateTime,
                                        endTime As DateTime) As Task

        Try

            If String.IsNullOrWhiteSpace(title) Then
                title = "Unknown Movie"
            End If

            Dim plexName = BuildPlexName(title, startTime)
            Dim safeName = CleanFileName(plexName)

            Dim paddedStart = startTime.AddSeconds(-START_PADDING_SECONDS)
            Dim paddedEnd = endTime.AddSeconds(END_PADDING_SECONDS)

            Dim now = DateTime.Now
            Dim duration = Math.Max((paddedEnd - If(now < paddedStart, paddedStart, now)).TotalSeconds, 0)

            If duration < 60 Then
                Log("Skipped → too short")
                Return
            End If

            ' WAIT
            Dim delay = paddedStart - DateTime.Now
            If delay.TotalMilliseconds > 1000 Then
                Await Task.Delay(delay)
            End If

            Directory.CreateDirectory(_recordingDir)

            Dim tempTs = Path.Combine(_recordingDir, safeName & ".ts")
            Dim tempMp4 = Path.Combine(_recordingDir, safeName & ".mp4")

            Dim streamUrl = $"{_epgUrl}live/{_epgUser}/{_epgPass}/{streamId}.ts"

            ' =========================================================
            ' RECORD
            ' =========================================================
            Dim recordOk = Await RunFFmpegRecord(streamUrl, tempTs, CInt(duration), plexName, startTime)

            If Not recordOk Then
                FailRecording(title, startTime, "ffmpeg failed")
                Return
            End If

            ' =========================================================
            ' VALIDATE
            ' =========================================================
            If Not ValidateRecording(tempTs) Then
                FailRecording(title, startTime, "invalid file")
                SafeDelete(tempTs)
                Return
            End If

            ' =========================================================
            ' CONVERT (SMALL FILE)
            ' =========================================================
            Dim convertOk = Await ConvertToMp4(tempTs, tempMp4, startTime, plexName)

            If Not convertOk Then
                FailRecording(title, startTime, "convert failed")
                SafeDelete(tempTs)
                SafeDelete(tempMp4)
                Return
            End If

            ' ✅ NEW: Validate MP4 output
            If Not File.Exists(tempMp4) Then
                FailRecording(title, startTime, "mp4 missing")
                SafeDelete(tempTs)
                Return
            End If

            Dim fiMp4 As New FileInfo(tempMp4)

            If fiMp4.Length < 1000000 Then ' 1MB threshold
                Log("MP4 too small → conversion failed")
                FailRecording(title, startTime, "mp4 too small")
                SafeDelete(tempMp4)
                SafeDelete(tempTs)
                Return
            End If

            ' =========================================================
            ' MOVE TO PLEX
            ' =========================================================
            Dim movieFolder = Path.Combine(_plexMoviesPath, plexName)
            Directory.CreateDirectory(movieFolder)

            Dim finalPath = Path.Combine(movieFolder, safeName & ".mp4")

            File.Move(tempMp4, finalPath, True)

            Log("COMPLETED → " & finalPath)

            UpdateRecordingStatus(title, startTime, "completed")

        Catch ex As Exception
            Log("ERROR → " & ex.Message)
            UpdateRecordingStatus(title, startTime, "failed")
        End Try

    End Function

    ' =========================================================
    ' RECORD TS
    ' =========================================================
    Private Async Function RunFFmpegRecord(url As String,
                                           output As String,
                                           duration As Integer,
                                           title As String,
                                           startTime As DateTime) As Task(Of Boolean)

        Dim args =
$"-y -nostdin -loglevel error " &
$"-user_agent ""{_userAgent}"" " &
$"-thread_queue_size 1024 " &
$"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 10 " &
$"-reconnect_at_eof 1 " &
$"-fflags +discardcorrupt " &
$"-err_detect ignore_err " &
$"-avoid_negative_ts make_zero " &
$"-i ""{url}"" " &
$"-t {duration} " &
$"-c copy " &
$"-f mpegts " &
$"""" & output & """"

        Return Await RunProcess(_ffmpegPath, args, title, startTime, "RECORD")

    End Function

    ' =========================================================
    ' CONVERT TO MP4
    ' =========================================================
    Private Async Function ConvertToMp4(inputTs As String,
                                        outputMp4 As String,
                                        startTime As DateTime,
                                        title As String) As Task(Of Boolean)
        Dim args = ""
        'Copy (no encoding)
        args =
$"-y -fflags +discardcorrupt -err_detect ignore_err " &
$"-i ""{inputTs}"" " &
$"-c copy " &
$"-movflags +faststart " &
$"""" & outputMp4 & """"
        GoTo skipOpt
        args =
$"-y -fflags +discardcorrupt -err_detect ignore_err " &
$"-i ""{inputTs}"" " &
$"-c:v libx264 -preset ultrafast -crf 26 " &
$"-c:a aac -b:a 128k " &
$"-movflags +faststart " &
$"""" & outputMp4 & """"
skipOpt:
        Return Await RunProcess(_ffmpegPath, args, title, startTime, "CONVERT")

    End Function

    ' =========================================================
    ' PROCESS RUNNER
    ' =========================================================
    Private Async Function RunProcess(exe As String,
                                 args As String,
                                 title As String,
                                 startTime As DateTime,
                                 stage As String) As Task(Of Boolean)

        Dim p As New Process()

        p.StartInfo.FileName = exe
        p.StartInfo.Arguments = args
        p.StartInfo.UseShellExecute = False
        p.StartInfo.CreateNoWindow = True
        p.StartInfo.RedirectStandardError = True
        p.StartInfo.RedirectStandardOutput = True

        Log($"{stage} START → {title}")
        Log($"{stage} CMD → {exe} {args}")

        p.Start()
        pid = p.Id
        UpdateRecordingPID(_DbPath, title, startTime, pid)
        Dim stderr = Await p.StandardError.ReadToEndAsync()
        Dim stdout = Await p.StandardOutput.ReadToEndAsync()

        Await p.WaitForExitAsync()
        ' Recording finished → clear PID
        UpdateRecordingPID(_DbPath, title, startTime, Nothing)

        If Not String.IsNullOrWhiteSpace(stderr) Then
            Log($"{stage} STDERR → {stderr}")
        End If

        If p.ExitCode <> 0 Then
            Log($"{stage} FAILED → {title}")
            Return False
        End If

        Log($"{stage} OK → {title}")
        Return True

    End Function
    ' =========================================================
    ' VALIDATION
    ' =========================================================
    Private Function ValidateRecording(filePath As String) As Boolean

        If Not File.Exists(filePath) Then Return False

        Dim fi As New FileInfo(filePath)
        If fi.Length < 50000000 Then Return False

        Return True

    End Function

    ' =========================================================
    ' TITLE CLEANING
    ' =========================================================
    Private Function NormalizeTitle(title As String) As String

        Dim t = title

        t = t.Replace("HD", "").Replace("SD", "").Replace("FHD", "").Replace("UHD", "")

        t = Regex.Replace(t, "\[.*?\]", "")
        t = Regex.Replace(t, "\(\d{4}\)", "")
        t = Regex.Replace(t, "\s+", " ").Trim()

        Return t

    End Function

    Private Function BuildPlexName(title As String, startTime As DateTime) As String

        Dim cleanTitle = NormalizeTitle(title)
        Dim year = startTime.Year

        Return $"{cleanTitle} ({year})"

    End Function

    Private Function CleanFileName(name As String) As String
        Return String.Concat(name.Where(Function(c) Not Path.GetInvalidFileNameChars().Contains(c))).Trim()
    End Function

    ' =========================================================
    ' HELPERS
    ' =========================================================
    Private Sub SafeDelete(path As String)
        Try
            If File.Exists(path) Then File.Delete(path)
        Catch
        End Try
    End Sub

    Private Sub FailRecording(title As String, startTime As DateTime, reason As String)
        Log("FAILED → " & title & " | " & reason)
        UpdateRecordingStatus(title, startTime, "failed")
    End Sub

    ' =========================================================
    ' DB UPDATE
    ' =========================================================
    Public Sub UpdateRecordingStatus(title As String,
                                     startTime As DateTime,
                                     status As String)

        Using con As New SqliteConnection($"Data Source={_DbPath}")
            con.Open()

            Dim cmd As New SqliteCommand("
UPDATE scheduled_recordings
SET status=@status
WHERE title=@title
AND start_time=@start
", con)

            cmd.Parameters.AddWithValue("@status", status)
            cmd.Parameters.AddWithValue("@title", title)
            cmd.Parameters.AddWithValue("@start", startTime)

            cmd.ExecuteNonQuery()
        End Using

    End Sub

    ' =========================================================
    ' LOGGING
    ' =========================================================
    Private Sub Log(msg As String)
        File.AppendAllText(LOG_FILE,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {msg}" & Environment.NewLine)
    End Sub

    ' =========================================================
    ' UI SUPPORT
    ' =========================================================
    Public Function ActiveRecordingCount() As Integer
        SyncLock _activeRecordings
            Return _activeRecordings.Count
        End SyncLock
    End Function

    Public Sub StopAllRecordings()
        SyncLock _activeRecordings
            For Each kv In _activeRecordings
                Try
                    If Not kv.Value.HasExited Then kv.Value.Kill()
                Catch
                End Try
            Next
            _activeRecordings.Clear()
        End SyncLock
    End Sub
    Public Sub UpdateRecordingPID(dbPath As String,
                              title As String,
                              startTime As DateTime,
                              pid As Integer?)

        Using con As New SqliteConnection($"Data Source={dbPath}")
            con.Open()

            Using cmd As New SqliteCommand("
UPDATE scheduled_recordings
SET process_id = @pid
WHERE title = @title
AND start_time = @start
", con)
                If pid.HasValue Then
                    cmd.Parameters.AddWithValue("@pid", pid.Value)
                Else
                    cmd.Parameters.AddWithValue("@pid", DBNull.Value)
                End If
                cmd.Parameters.AddWithValue("@title", title)
                cmd.Parameters.AddWithValue("@start", startTime)

                cmd.ExecuteNonQuery()

            End Using

        End Using

    End Sub

End Module