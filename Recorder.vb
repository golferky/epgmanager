Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

Public Module Recorder

    Private Const START_PADDING_SECONDS As Integer = 60
    Private Const END_PADDING_SECONDS As Integer = 900

    'this allows 10 concurrent recordings, which is the max Plex Pass limit. If you have more tuners, you can increase this.    
    Private ReadOnly _recordingLimiter As New SemaphoreSlim(10)

    Public LOG_FILE As String = "/Users/garyscudder/epg/logs/recordings.log"

    Public Sub RecordMovie(title As String,
                       streamId As String,
                       startTime As DateTime,
                       endTime As DateTime)

        If Not _recordingLimiter.Wait(0) Then
            Log("SKIPPED → No recording slot → " & title)
            Return
        End If

        Task.Run(Sub()
                     Try
                         RunRecording(title, streamId, startTime, endTime)
                     Finally
                         _recordingLimiter.Release()
                     End Try
                 End Sub)

    End Sub

    Private _activeTitles As New HashSet(Of String)

    Private Sub RunRecording(title As String,
                             streamId As String,
                             startTime As DateTime,
                             endTime As DateTime)

        Console.WriteLine("Recorder slot acquired → " & title)

        Try

            ' Wait until airtime (with padding)
            Dim paddedStart = startTime.AddSeconds(-START_PADDING_SECONDS)
            Dim wait = paddedStart - DateTime.Now

            If wait.TotalSeconds > 0 Then
                Console.WriteLine($"Waiting {CInt(wait.TotalSeconds)} sec → {title}")
                Thread.Sleep(wait)
            End If

            ' Calculate recording duration
            Dim duration As Integer =
                CInt((endTime - startTime).TotalSeconds) +
                START_PADDING_SECONDS +
                END_PADDING_SECONDS

            If duration < 300 Then duration = 300

            Console.WriteLine($"Recording duration → {duration} sec")

            ' Clean title for filesystem
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

            ' Direct stream URL (stable for IPTV)
            Dim streamUrl =
                $"{_epgUrl}live/{_epgUser}/{_epgPass}/{streamId}.ts"

            Console.WriteLine("Starting recording → " & streamUrl)

            Dim args =
    $"-nostdin -loglevel info " &
    $"-user_agent ""{_userAgent}"" " &
    $"-thread_queue_size 1024 " &
    $"-reconnect 1 " &
    $"-reconnect_streamed 1 " &
    $"-reconnect_at_eof 1 " &
    $"-reconnect_delay_max 10 " &
    $"-fflags +discardcorrupt " &
    $"-err_detect ignore_err " &
    $"-i ""{streamUrl}"" " &
    $"-t {duration} " &
    $"-map 0 " &
    $"-c copy " &
    $"-movflags +faststart " &
    $"""{tmp}"""

            Dim p As New Process()

            p.EnableRaisingEvents = True

            p.StartInfo.FileName = _ffmpegPath
            p.StartInfo.Arguments = args
            p.StartInfo.UseShellExecute = False
            p.StartInfo.CreateNoWindow = True
            p.StartInfo.RedirectStandardError = True
            p.StartInfo.RedirectStandardOutput = True

            ' Capture ffmpeg logs
            AddHandler p.ErrorDataReceived,
            Sub(sender, e)
                If e.Data IsNot Nothing Then
                    Log("FFMPEG → " & e.Data)
                End If
            End Sub

            AddHandler p.OutputDataReceived,
            Sub(sender, e)
                If e.Data IsNot Nothing Then
                    Log("FFMPEG → " & e.Data)
                End If
            End Sub

            AddHandler p.Exited,
            Sub()
                Log("FFMPEG EXIT → " & title)
            End Sub

            Log("FFMPEG CMD → " & _ffmpegPath & " " & args)

            p.Start()

            p.BeginErrorReadLine()
            p.BeginOutputReadLine()

            Log("FFMPEG STARTED → PID " & p.Id & " | " & title)

            Console.WriteLine("Recording → " & safeTitle)

            ' Wait until ffmpeg completes
            p.WaitForExit()

            ' Rename temp file to final output
            If File.Exists(tmp) Then
                File.Move(tmp, output, True)
                Console.WriteLine("Completed → " & output)
                Log("COMPLETED → " & output)
            End If

        Catch ex As Exception

            Console.WriteLine("Recording error → " & ex.Message)
            Log("ERROR → " & ex.Message)

        Finally

        End Try

    End Sub

End Module