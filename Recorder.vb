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
                    Dim reason = $"Mac API error {CInt(response.StatusCode)}: {responseBody}"
                    Logger.Log($"MAC API ERROR → {response.StatusCode}", "Recorder", "RecordOnMac", "ERROR")
                    UpdateRecordingStatus(recJob, "failed", reason)
                End If

            End Using

        Catch ex As Exception
            Logger.Log($"RecordOnMac EXCEPTION → {ex.Message}", "Recorder", "RecordOnMac", "ERROR")
            Dim recJob As New RecordingJob With {
                .Title = scheduledTitle,
                .StartTime = startTime
            }
            UpdateRecordingStatus(recJob, "failed", "RecordOnMac exception: " & ex.Message)
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
                                Dim errorText = json("error")?.ToString()
                                UpdateRecordingStatus(job, "failed", errorText)
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
                UpdateRecordingStatus(job, "timeout", "Mac recorder polling timed out after 4 hours")

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
            UpdateRecordingStatus(job, "failed", $"{stage} failed with exit code {p.ExitCode}")
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
    Public Sub UpdateRecordingStatus(job As RecordingJob, status As String, Optional failureReason As String = Nothing)

        Logger.Log($"STATUS → {status} | {job.Title}", "Recorder", "UpdateRecordingStatus")

        SyncLock GlobalState.DbLock
            Using con As New SqliteConnection($"Data Source={_DbPath}")
                con.Open()
                EnsureRecordingFailureColumns(con)
                EnsureChannelHealthTables(con)

                Dim cmd As New SqliteCommand("
                    UPDATE scheduled_recordings
                    SET status = @status,
                        job_id = @jobId,
                        failure_reason = CASE
                            WHEN @failureReason IS NOT NULL AND @failureReason <> '' THEN @failureReason
                            WHEN @status IN ('scheduled','queued','recording','completed','complete','cancelled','stopped') THEN NULL
                            ELSE failure_reason
                        END
                    WHERE title = @title
                    AND start_time = @start
                ", con)

                cmd.Parameters.AddWithValue("@status", status)
                cmd.Parameters.AddWithValue("@title", job.Title)
                cmd.Parameters.AddWithValue("@start", job.StartTime)
                cmd.Parameters.AddWithValue("@jobId", job.Jobid)
                cmd.Parameters.AddWithValue("@failureReason", If(String.IsNullOrWhiteSpace(failureReason), CObj(DBNull.Value), CObj(failureReason)))

                cmd.ExecuteNonQuery()

                If status = "failed" AndAlso IsProviderFailure(failureReason) Then
                    RecordChannelFailure(con, job, failureReason)
                End If
            End Using
        End SyncLock

    End Sub

    Private Sub EnsureRecordingFailureColumns(con As SqliteConnection)
        Using cmd As New SqliteCommand("
            ALTER TABLE scheduled_recordings ADD COLUMN failure_reason TEXT;
        ", con)
            Try
                cmd.ExecuteNonQuery()
            Catch ex As SqliteException When ex.SqliteErrorCode = 1 AndAlso ex.Message.IndexOf("duplicate column name", StringComparison.OrdinalIgnoreCase) >= 0
            End Try
        End Using
    End Sub

    Private Sub EnsureChannelHealthTables(con As SqliteConnection)
        Using cmd As New SqliteCommand("
            CREATE TABLE IF NOT EXISTS channel_recording_failures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel TEXT NOT NULL,
                job_id TEXT,
                title TEXT,
                start_time TEXT,
                failure_reason TEXT,
                failed_at TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                UNIQUE(job_id)
            );

            CREATE TABLE IF NOT EXISTS channel_health (
                channel TEXT PRIMARY KEY,
                failed_count_7_days INTEGER NOT NULL DEFAULT 0,
                last_failed_at TEXT,
                last_failure_reason TEXT,
                is_suspect INTEGER NOT NULL DEFAULT 0,
                suspect_until TEXT
            );
        ", con)
            cmd.ExecuteNonQuery()
        End Using
    End Sub

    Private Function IsProviderFailure(reason As String) As Boolean
        If String.IsNullOrWhiteSpace(reason) Then Return False

        Dim r = reason.ToLowerInvariant()
        Return r.Contains("403") OrElse
               r.Contains("404") OrElse
               r.Contains("forbidden") OrElse
               r.Contains("access denied") OrElse
               r.Contains("error opening input") OrElse
               r.Contains("connection") OrElse
               r.Contains("timed out") OrElse
               r.Contains("timeout") OrElse
               r.Contains("output file missing") OrElse
               r.Contains("too small")
    End Function

    Private Sub RecordChannelFailure(con As SqliteConnection, job As RecordingJob, reason As String)
        Dim channel As String = Nothing
        Dim startText = job.StartTime.ToString("yyyy-MM-dd HH:mm:ss")

        Using find As New SqliteCommand("
            SELECT channel
            FROM scheduled_recordings
            WHERE title = @title
            AND start_time = @start
            LIMIT 1", con)
            find.Parameters.AddWithValue("@title", job.Title)
            find.Parameters.AddWithValue("@start", startText)
            Dim result = find.ExecuteScalar()
            If result IsNot Nothing AndAlso result IsNot DBNull.Value Then channel = result.ToString()
        End Using

        If String.IsNullOrWhiteSpace(channel) Then Return

        Using insertFailure As New SqliteCommand("
            INSERT OR IGNORE INTO channel_recording_failures
                (channel, job_id, title, start_time, failure_reason)
            VALUES
                (@channel, @jobId, @title, @start, @reason)", con)
            insertFailure.Parameters.AddWithValue("@channel", channel)
            insertFailure.Parameters.AddWithValue("@jobId", If(String.IsNullOrWhiteSpace(job.Jobid), CObj(DBNull.Value), CObj(job.Jobid)))
            insertFailure.Parameters.AddWithValue("@title", job.Title)
            insertFailure.Parameters.AddWithValue("@start", startText)
            insertFailure.Parameters.AddWithValue("@reason", If(String.IsNullOrWhiteSpace(reason), CObj(DBNull.Value), CObj(reason)))
            insertFailure.ExecuteNonQuery()
        End Using

        Dim failedCount As Integer
        Using countCmd As New SqliteCommand("
            SELECT COUNT(*)
            FROM channel_recording_failures
            WHERE channel = @channel
            AND failed_at >= datetime('now','localtime','-7 days')", con)
            countCmd.Parameters.AddWithValue("@channel", channel)
            failedCount = Convert.ToInt32(countCmd.ExecuteScalar())
        End Using

        Using updateHealth As New SqliteCommand("
            INSERT INTO channel_health
                (channel, failed_count_7_days, last_failed_at, last_failure_reason, is_suspect, suspect_until)
            VALUES
                (@channel, @count, datetime('now','localtime'), @reason, @suspect,
                 CASE WHEN @suspect = 1 THEN datetime('now','localtime','+7 days') ELSE NULL END)
            ON CONFLICT(channel) DO UPDATE SET
                failed_count_7_days = excluded.failed_count_7_days,
                last_failed_at = excluded.last_failed_at,
                last_failure_reason = excluded.last_failure_reason,
                is_suspect = excluded.is_suspect,
                suspect_until = excluded.suspect_until", con)
            updateHealth.Parameters.AddWithValue("@channel", channel)
            updateHealth.Parameters.AddWithValue("@count", failedCount)
            updateHealth.Parameters.AddWithValue("@reason", If(String.IsNullOrWhiteSpace(reason), CObj(DBNull.Value), CObj(reason)))
            updateHealth.Parameters.AddWithValue("@suspect", If(failedCount >= 2, 1, 0))
            updateHealth.ExecuteNonQuery()
        End Using
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
