Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

Public Module Recorder

    Private Const START_PADDING_SECONDS As Integer = 60
    Private Const END_PADDING_SECONDS As Integer = 300

    Private ReadOnly _recordingLimiter As New SemaphoreSlim(3)

    Public Sub RecordMovie(title As String,
                           streamId As String,
                           startTime As DateTime,
                           endTime As DateTime)

        Task.Run(Sub() RunRecording(title, streamId, startTime, endTime))

    End Sub

    Private Sub RunRecording(title As String,
                             streamId As String,
                             startTime As DateTime,
                             endTime As DateTime)

        ' Wait until airtime (with padding)
        Dim paddedStart = startTime.AddSeconds(-START_PADDING_SECONDS)
        Dim wait = paddedStart - DateTime.Now

        If wait.TotalSeconds > 0 Then
            Console.WriteLine($"Waiting {CInt(wait.TotalSeconds)} sec → {title}")
            Thread.Sleep(wait)
        End If

        ' Limit concurrent recordings
        Console.WriteLine("Waiting for recorder slot → " & title)
        _recordingLimiter.Wait()

        Console.WriteLine("Recorder slot acquired → " & title)

        Try

            Dim duration As Integer =
                CInt((endTime - startTime).TotalSeconds) +
                START_PADDING_SECONDS +
                END_PADDING_SECONDS

            If duration < 300 Then duration = 300

            Console.WriteLine($"Recording duration → {duration} sec")

            ' Clean title
            Dim safeTitle = title.Replace(":", "") _
                                 .Replace("/", "") _
                                 .Replace("?", "") _
                                 .Replace("*", "") _
                                 .Trim()

            For Each c In Path.GetInvalidFileNameChars()
                safeTitle = safeTitle.Replace(c, "")
            Next

            Dim movieFolder = Path.Combine(_plexMoviesPath, safeTitle)

            If Not Directory.Exists(movieFolder) Then
                Directory.CreateDirectory(movieFolder)
            End If

            Dim tmp = Path.Combine(movieFolder, safeTitle & ".tmp.mp4")
            Dim output = Path.Combine(movieFolder, safeTitle & ".mp4")

            Dim streamUrl =
                $"{_epgUrl}live/{_epgUser}/{_epgPass}/{streamId}.m3u8"

            Console.WriteLine("Starting recording → " & streamUrl)

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
$"-c:v copy " &
$"-c:a copy " &
$"-movflags +faststart " &
$"""{tmp}"""

            Dim p As New Process()

            p.StartInfo.FileName = _ffmpegPath
            p.StartInfo.Arguments = args
            p.StartInfo.UseShellExecute = False
            p.StartInfo.CreateNoWindow = True

            p.Start()

            Console.WriteLine("Recording → " & safeTitle)

            p.WaitForExit()

            If File.Exists(tmp) Then
                File.Move(tmp, output, True)
                Console.WriteLine("Completed → " & output)
            End If

        Catch ex As Exception

            Console.WriteLine("Recording error → " & ex.Message)

        Finally

            _recordingLimiter.Release()

        End Try

    End Sub

End Module