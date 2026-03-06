Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

Public Module Recorder

    Private ReadOnly _scheduledTitles As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private Const START_PADDING_SECONDS As Integer = 60
    Private Const END_PADDING_SECONDS As Integer = 300

    Private _activeRecordings As Integer = 0
    Private ReadOnly _recordingLimiter As New SemaphoreSlim(3)

    Public Sub RecordMovie(title As String,
                       streamId As String,
                       startTime As DateTime,
                       endTime As DateTime)

        Dim normalized = NormalizeTitle(title)

        SyncLock _scheduledTitles

            If _scheduledTitles.Contains(normalized) Then
                Console.WriteLine("Skipping duplicate → " & title)
                Return
            End If

            _scheduledTitles.Add(normalized)

        End SyncLock

        Task.Run(Sub() RunRecording(title, streamId, startTime, endTime))

    End Sub

    Private Sub RunRecording(title As String,
                             streamId As String,
                             startTime As DateTime,
                             endTime As DateTime)

        ' wait until airtime
        Dim paddedStart = startTime.AddSeconds(-START_PADDING_SECONDS)

        Dim wait = paddedStart - DateTime.Now

        If wait.TotalSeconds > 0 Then
            Thread.Sleep(wait.TotalMilliseconds)
        End If
        ' throttle
        Console.WriteLine("Waiting for recorder slot → " & title)

        _recordingLimiter.WaitAsync().Wait()

        Console.WriteLine("Recorder slot acquired → " & title)

        Try
            Dim duration =
    CInt((endTime - DateTime.Now).TotalSeconds) +
    END_PADDING_SECONDS

            If duration < 300 Then duration = 300

            Dim normalized = NormalizeTitle(title)
            Dim year = ExtractYear(normalized)
            Dim cleanTitle = RemoveYear(normalized)

            Dim safeTitle =
                cleanTitle.Replace(":", "") _
                          .Replace("/", "") _
                          .Replace("?", "") _
                          .Replace("*", "") _
                          .Trim()

            For Each c In Path.GetInvalidFileNameChars()
                safeTitle = safeTitle.Replace(c, "")
            Next

            If year.HasValue Then
                safeTitle &= " (" & year.Value.ToString() & ")"
            End If

            Dim movieFolder =
                Path.Combine(_plexMoviesPath, safeTitle)

            If Not Directory.Exists(movieFolder) Then
                Directory.CreateDirectory(movieFolder)
            End If

            Dim tmp =
                Path.Combine(movieFolder, safeTitle & ".tmp.mp4")

            Dim output =
                Path.Combine(movieFolder, safeTitle & ".mp4")

            Dim streamUrl =
                $"{_epgUrl}live/{_epgUser}/{_epgPass}/{streamId}.m3u8"

            Dim args =
$"-nostdin -loglevel error " &
$"-user_agent ""{_userAgent}"" " &
$"-thread_queue_size 1024 " &
$"-reconnect 1 " &
$"-reconnect_streamed 1 " &
$"-reconnect_delay_max 5 " &
$"-fflags +discardcorrupt " &
$"-err_detect ignore_err " &
$"-i ""{streamUrl}"" " &
$"-t {duration} " &
$"-map 0 " &
$"-c copy " &
$"-bsfs:a aac_adtstoasc " &
$"-movflags +faststart " &
$"""{tmp}"""

            _activeRecordings += 1

            Console.WriteLine($"Recording duration → {duration} seconds")

            Dim p As New Process

            p.StartInfo.FileName = _ffmpegPath
            p.StartInfo.Arguments = args
            p.StartInfo.UseShellExecute = False
            p.StartInfo.CreateNoWindow = True

            Console.WriteLine("Recording → " & safeTitle)

            p.Start()
            p.WaitForExit()

            If File.Exists(tmp) Then
                File.Move(tmp, output)
                Console.WriteLine("Completed → " & output)
            End If

        Finally

            _recordingLimiter.Release()

        End Try

    End Sub

End Module