Imports System.IO
Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports System.Text.RegularExpressions
Imports System.Net.Http

Public Module Recorder

    Private Const START_PADDING_SECONDS As Integer = 60
    Private Const END_PADDING_SECONDS As Integer = 120

    Private ReadOnly _recordingLimiter As New SemaphoreSlim(10)
    Public _activeRecordings As New Dictionary(Of String, Process)
    Public ReadOnly _lock As New Object()
    Public pid As Integer

    Public Class RecordingJob
        Public Property Jobid As String
        Public Property Title As String
        Public Property StartTime As DateTime
    End Class

    ' =========================================================
    ' ENTRY POINT
    ' =========================================================
    Public Sub RecordMovie(title As String,
                       streamId As String,
                       startTime As DateTime,
                       endTime As DateTime,
                       Optional programType As String = "",
                       Optional seasonNumber As Integer = 0,
                       Optional episodeNumber As Integer = 0,
                       Optional episodeTitle As String = "")

        _recordingLimiter.Wait()

        Try
            RunRecording(title, streamId, startTime, endTime, programType, seasonNumber, episodeNumber, episodeTitle)
        Finally
            _recordingLimiter.Release()
        End Try

    End Sub

    Public Function GenerateJobId() As String
        Dim timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")
        Dim shortGuid = Guid.NewGuid().ToString("N").Substring(0, 6)
        Return $"job_{shortGuid}"
    End Function

    ' =========================================================
    ' MAIN PIPELINE
    ' =========================================================
    Private Sub RunRecording(title As String,
                         streamId As String,
                         startTime As DateTime,
                         endTime As DateTime,
                         Optional programType As String = "",
                         Optional seasonNumber As Integer = 0,
                         Optional episodeNumber As Integer = 0,
                         Optional episodeTitle As String = "")

        Dim job As New RecordingJob With {
            .Jobid = GenerateJobId(),
            .Title = title,
            .StartTime = startTime
        }

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
            Dim skipReason As String = Nothing

            If duration < 60 Then
                skipReason = "skipped → too short"
            End If

            If skipReason IsNot Nothing Then
                Try
                    UpdateRecordingStatus(job, skipReason)
                Catch
                End Try
                Try
                    Logger.Log(skipReason, "Recorder", "RunRecording")
                Catch
                End Try
                Return
            End If

            ' WAIT FOR START
            Dim delay = paddedStart - DateTime.Now
            If delay.TotalMilliseconds > 1000 Then
                Thread.Sleep(delay)
            End If

            ' =========================================================
            ' 🍎 MAC PIPELINE (REST API)
            ' =========================================================
            If GlobalState.CurrentTarget = ExecutionTarget.RemoteMac Then
                RecordOnMac(streamId, title, safeName, CInt(duration), startTime,
                programType, seasonNumber, episodeNumber, episodeTitle)
                Return
            End If

            ' =========================================================
            ' 🪟 WINDOWS PIPELINE
            ' =========================================================
            Dim streamUrl = $"{_epgUrl}live/{_epgUser}/{_epgPass}/{streamId}.ts"

            Directory.CreateDirectory(_recordingDir)

            Dim tempTs = Path.Combine(_recordingDir, safeName & ".ts")
            Dim tempMp4 = Path.Combine(_recordingDir, safeName & ".mp4")

            If Not IsStreamValid(streamUrl) Then
                FailRecording(job, "invalid stream")
                Return
            End If

            RunFFmpegRecord(streamUrl, tempTs, CInt(duration), job)

            If Not ValidateRecording(tempTs) Then
                FailRecording(job, "invalid file")
                SafeDelete(tempTs)
                Return
            End If

            ConvertToMp4(tempTs, tempMp4, job)

            If Not File.Exists(tempMp4) Then
                FailRecording(job, "mp4 missing")
                Return
            End If

            Dim fiMp4 As New FileInfo(tempMp4)
            If fiMp4.Length < 1000000 Then
                FailRecording(job, "mp4 too small")
                SafeDelete(tempMp4)
                Return
            End If

            Dim movieFolder = Path.Combine(_plexMoviesPath, plexName)
            Directory.CreateDirectory(movieFolder)

            Dim finalPath = Path.Combine(movieFolder, safeName & ".mp4")
            File.Move(tempMp4, finalPath, True)

            Logger.Log("COMPLETED → " & finalPath)
            UpdateRecordingStatus(job, "completed")

        Catch ex As Exception
            Logger.Log("ERROR → " & ex.Message)
            UpdateRecordingStatus(job, "failed")
        End Try

    End Sub

    ' =========================================================
    ' 🍎 MAC RECORDING (REST API)
    ' =========================================================
    Private Sub RecordOnMac(streamId As String,
                        scheduledTitle As String,
                        recordingTitle As String,
                        duration As Integer,
                        startTime As DateTime,
                        Optional programType As String = "",
                        Optional seasonNumber As Integer = 0,
                        Optional episodeNumber As Integer = 0,
                        Optional episodeTitle As String = "")
        Try
            Dim jobId = GenerateJobId()
            Dim recJob As New RecordingJob With {
                .Jobid = jobId,
                .Title = scheduledTitle,
                .StartTime = startTime
            }

            Dim streamUrl = $"{_epgUrl}live/{_epgUser}/{_epgPass}/{streamId}.ts"

            ' Build JSON payload
            Dim payload = Newtonsoft.Json.JsonConvert.SerializeObject(New With {
    .job_id = jobId,
    .title = recordingTitle,
    .url = streamUrl,
    .duration = duration,
    .start_time = startTime.ToString("s"),
    .program_type = programType,
    .season_number = seasonNumber,
    .episode_number = episodeNumber,
    .episode_title = episodeTitle
})

            ' POST to Mac Flask API
            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(10)

                Dim content = New StringContent(
                    payload,
                    System.Text.Encoding.UTF8,
                    "application/json")

                Dim macApiUrl = $"http://{GlobalState.MacHost}:{GlobalState.MacPort}/record"

                Logger.Log($"POST → {macApiUrl} | {recordingTitle}", "Recorder", "RecordOnMac")

                Dim response = client.PostAsync(macApiUrl, content).Result
                Dim responseBody = response.Content.ReadAsStringAsync().Result

                Logger.Log($"MAC RESPONSE → {responseBody}", "Recorder", "RecordOnMac")

                If response.IsSuccessStatusCode Then
                    UpdateRecordingStatus(recJob, "queued")
                    Logger.Log($"QUEUED ON MAC → {recordingTitle}", "Recorder", "RecordOnMac")

                    ' Poll for completion on background thread
                    Dim pollJob = recJob
                    Dim t As New Thread(Sub() PollJobStatus(pollJob))
                    t.IsBackground = True
                    t.Start()

                Else
                    Logger.Log($"MAC API ERROR → {response.StatusCode}", "Recorder", "RecordOnMac", "ERROR")
                    UpdateRecordingStatus(recJob, "failed")
                End If

            End Using

        Catch ex As Exception
            Logger.Log($"RecordOnMac EXCEPTION → {ex.Message}", "Recorder", "RecordOnMac", "ERROR")
        End Try
    End Sub

    ' =========================================================
    ' POLL MAC FOR JOB COMPLETION
    ' =========================================================
    Private Sub PollJobStatus(job As RecordingJob)
        Try
            Dim macApiUrl = $"http://{GlobalState.MacHost}:{GlobalState.MacPort}/status/{job.Jobid}"
            Dim timeout = DateTime.Now.AddHours(4)

            Using client As New HttpClient()

                While DateTime.Now < timeout

                    Thread.Sleep(30000) ' check every 30 seconds

                    Try
                        Dim response = client.GetAsync(macApiUrl).Result
                        Dim body = response.Content.ReadAsStringAsync().Result
                        Dim json = Newtonsoft.Json.Linq.JObject.Parse(body)
                        Dim statusVal = json("status")?.ToString()

                        Logger.Log($"POLL → {job.Title} | {statusVal}", "Recorder", "PollJobStatus")

                        Select Case statusVal
                            Case "done"
                                UpdateRecordingStatus(job, "completed")
                                Return
                            Case "failed"
                                UpdateRecordingStatus(job, "failed")
                                Return
                            Case "cancelled"
                                UpdateRecordingStatus(job, "cancelled")
                                Return
                            Case "queued", "recording"
                                ' Still running, keep polling
                            Case Else
                                Logger.Log($"UNKNOWN STATUS → {statusVal}", "Recorder", "PollJobStatus", "WARN")
                        End Select

                    Catch ex As Exception
                        Logger.Log($"POLL ERROR → {ex.Message}", "Recorder", "PollJobStatus", "WARN")
                    End Try

                End While

                ' Timed out after 4 hours
                Logger.Log($"POLL TIMEOUT → {job.Title}", "Recorder", "PollJobStatus", "ERROR")
                UpdateRecordingStatus(job, "timeout")

            End Using

        Catch ex As Exception
            Logger.Log($"PollJobStatus EXCEPTION → {ex.Message}", "Recorder", "PollJobStatus", "ERROR")
        End Try
    End Sub

    ' =========================================================
    ' WINDOWS FFMPEG
    ' =========================================================
    Private Sub RunFFmpegRecord(url As String,
                                output As String,
                                duration As Integer,
                                job As RecordingJob)

        Dim args = $"-y -loglevel error -i ""{url}"" -t {duration} -c copy -f mpegts ""{output}"""
        RunProcess(_ffmpegPath, args, job, "RECORD")

    End Sub

    Private Sub ConvertToMp4(inputTs As String,
                             outputMp4 As String,
                             job As RecordingJob)

        Dim args = $"-i ""{inputTs}"" -c copy -movflags +faststart ""{outputMp4}"""
        RunProcess(_ffmpegPath, args, job, "CONVERT")

    End Sub

    ' =========================================================
    ' PROCESS RUNNER (WINDOWS ONLY)
    ' =========================================================
    Private Sub RunProcess(exe As String,
                           args As String,
                           job As RecordingJob,
                           stage As String)

        Dim p As New Process()
        p.StartInfo.FileName = exe
        p.StartInfo.Arguments = args
        p.StartInfo.UseShellExecute = False
        p.StartInfo.CreateNoWindow = True
        p.StartInfo.RedirectStandardError = True
        p.StartInfo.RedirectStandardOutput = True

        Logger.Log($"{stage} START → {job.Title}")

        p.Start()

        Dim key = $"{job.Title}_{job.StartTime:yyyyMMddHHmm}"

        SyncLock _activeRecordings
            _activeRecordings(key) = p
        End SyncLock

        p.WaitForExit()

        SyncLock _activeRecordings
            _activeRecordings.Remove(key)
        End SyncLock

        If p.ExitCode <> 0 Then
            Logger.Log($"{stage} FAILED → {job.Title}")
            UpdateRecordingStatus(job, "failed")
        Else
            Logger.Log($"{stage} OK → {job.Title}")
            UpdateRecordingStatus(job, "complete")
        End If

    End Sub

    ' =========================================================
    ' HELPERS
    ' =========================================================
    Private Function IsStreamValid(url As String) As Boolean
        Try
            Dim req = CType(Net.WebRequest.Create(url), Net.HttpWebRequest)
            req.Method = "HEAD"
            req.Timeout = 2000
            Using res = req.GetResponse()
            End Using
            Return True
        Catch
            Return False
        End Try
    End Function

    Private Function ValidateRecording(filePath As String) As Boolean
        If Not File.Exists(filePath) Then Return False
        Return New FileInfo(filePath).Length > 50000000
    End Function

    Private Function NormalizeTitle(title As String) As String
        Dim t = Regex.Replace(title, "\(\d{4}\)", "")
        Return Regex.Replace(t, "\s+", " ").Trim()
    End Function

    Private Function BuildPlexName(title As String, startTime As DateTime) As String
        Dim normalized = NormalizeTitle(title)
        Dim year As String = Nothing

        ' Look up real release year from DB
        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={_DbPath}")
                con.Open()
                Dim cmd As New SqliteCommand("
                SELECT year FROM master_titles
                WHERE title = @title
                AND year IS NOT NULL
                AND year != ''
                LIMIT 1", con)
                cmd.Parameters.AddWithValue("@title", normalized)
                Dim result = cmd.ExecuteScalar()
                If result IsNot Nothing Then
                    year = result.ToString()
                End If
            End Using
        End SyncLock

        ' Fall back to recording year if not found
        Return $"{normalized} ({If(year, startTime.Year.ToString())})"
    End Function

    Private Function CleanFileName(name As String) As String
        Return String.Concat(name.Where(Function(c) Not Path.GetInvalidFileNameChars().Contains(c))).Trim()
    End Function

    Private Sub SafeDelete(path As String)
        Try
            If File.Exists(path) Then File.Delete(path)
        Catch
        End Try
    End Sub

    Private Sub FailRecording(job As RecordingJob, reason As String)
        Logger.Log("FAILED → " & job.Title & " | " & reason)
        UpdateRecordingStatus(job, "failed")
    End Sub

    ' =========================================================
    ' DB STATUS UPDATE
    ' =========================================================
    Public Sub UpdateRecordingStatus(job As RecordingJob, status As String)

        Logger.Log($"STATUS → {status} | {job.Title}", "Recorder", "UpdateRecordingStatus")

        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={_DbPath}")
                con.Open()

                Dim cmd As New SqliteCommand("
                    UPDATE scheduled_recordings
                    SET status = @status,
                        job_id = @jobId
                    WHERE title = @title
                    AND start_time = @start
                ", con)

                cmd.Parameters.AddWithValue("@status", status)
                cmd.Parameters.AddWithValue("@title", job.Title)
                cmd.Parameters.AddWithValue("@start", job.StartTime)
                cmd.Parameters.AddWithValue("@jobId", job.Jobid)

                cmd.ExecuteNonQuery()
            End Using
        End SyncLock

    End Sub

    ' =========================================================
    ' UI SUPPORT
    ' =========================================================
    Public Function ActiveRecordingCount() As Integer
        SyncLock _activeRecordings
            Return _activeRecordings.Count
        End SyncLock
    End Function

    Public Sub StopRecording(title As String, startTime As DateTime)

        Dim key = $"{title}_{startTime:yyyyMMddHHmm}"

        SyncLock _activeRecordings
            If _activeRecordings.ContainsKey(key) Then

                Dim proc = _activeRecordings(key)

                Try
                    If proc IsNot Nothing AndAlso Not proc.HasExited Then
                        proc.Kill()
                        Dim job As New RecordingJob With {
                            .Jobid = proc.Id.ToString(),
                            .Title = title,
                            .StartTime = startTime
                        }
                        UpdateRecordingStatus(job, "stopped")
                    End If
                Catch
                End Try

                _activeRecordings.Remove(key)
            End If
        End SyncLock

    End Sub

    Public Sub StopAllRecordings()

        SyncLock _activeRecordings

            For Each kv In _activeRecordings
                Try
                    If kv.Value IsNot Nothing AndAlso Not kv.Value.HasExited Then
                        kv.Value.Kill()
                    End If
                Catch
                End Try
            Next

            _activeRecordings.Clear()

        End SyncLock

    End Sub

End Module
