Public Class TiviMateService

    Public Shared Function RunManualSync(config As ConfigManager) As List(Of TiviRecording)

        Console.WriteLine("Starting TiviMate sync...")

        Dim ip = config.GetString("FIRESTICK_IP")
        Dim adbExe = config.GetAdbPath()
        Dim backupDir = config.GetPath("TIVIMATE_BACKUP")

        If String.IsNullOrWhiteSpace(ip) Then
            Throw New Exception("FIRESTICK_IP missing from config")
        End If

        If Not IO.Directory.Exists(backupDir) Then
            Throw New Exception("Backup directory not accessible: " & backupDir)
        End If

        AdbTriggerBackup(ip, adbExe)

        Dim backup = WaitForBackupFile(backupDir)

        Dim recs = TiviMateParser.LoadBackup(backup)

        Console.WriteLine("Finished. Found " & recs.Count & " items.")

        Return recs

    End Function
    Private Shared Sub AdbTriggerBackup(ip As String, adbExe As String)

        Dim dev = ip & ":5555"

        Console.WriteLine("Resetting UI state...")

        ' Ensure predictable starting state
        For i = 1 To 3
            Run(adbExe, $"-s {dev} shell input keyevent 4")
            Threading.Thread.Sleep(400)
        Next

        Threading.Thread.Sleep(800)

        Console.WriteLine("Opening sidebar...")

        ' Open sidebar
        For i = 1 To 3
            Run(adbExe, $"-s {dev} shell input keyevent 21")
            Threading.Thread.Sleep(500)
        Next

        Console.WriteLine("Opening Settings...")

        ' Move to Settings
        For i = 1 To 3
            Run(adbExe, $"-s {dev} shell input keyevent 20")
            Threading.Thread.Sleep(400)
        Next

        Run(adbExe, $"-s {dev} shell input keyevent 23") ' enter Settings
        Threading.Thread.Sleep(2000)

        Console.WriteLine("Opening General...")

        ' Enter General
        Run(adbExe, $"-s {dev} shell input keyevent 23")
        Threading.Thread.Sleep(1500)

        Console.WriteLine("Navigating to Backup...")

        ' Down to Backup Data
        For i = 1 To 6
            Run(adbExe, $"-s {dev} shell input keyevent 20")
            Threading.Thread.Sleep(350)
        Next

        Console.WriteLine("Starting backup...")

        Console.WriteLine("Selecting NAS storage...")

        ' DOWN once (switch from Internal Storage)
        Run(adbExe, $"-s {dev} shell input keyevent 20")
        Threading.Thread.Sleep(400)

        ' ENTER to open storage locations
        Run(adbExe, $"-s {dev} shell input keyevent 23")
        Threading.Thread.Sleep(1500)

        Console.WriteLine("Navigating to My Stuff...")

        Run(adbExe, $"-s {dev} shell input keyevent 20")

        Run(adbExe, $"-s {dev} shell input keyevent 23")
        ' to My Stuff
        For i = 1 To 13
            Run(adbExe, $"-s {dev} shell input keyevent 20")
            Threading.Thread.Sleep(250)
        Next

        Run(adbExe, $"-s {dev} shell input keyevent 23")
        Threading.Thread.Sleep(1500)

        Console.WriteLine("Navigating to TiviMate folder...")

        ' TiviMate folder is first
        Run(adbExe, $"-s {dev} shell input keyevent 20")
        Threading.Thread.Sleep(250)

        Run(adbExe, $"-s {dev} shell input keyevent 23")
        Threading.Thread.Sleep(1000)

        Console.WriteLine("Confirming Save...")

        ' RIGHT once to Save button
        Run(adbExe, $"-s {dev} shell input keyevent 22")
        Threading.Thread.Sleep(400)

        ' ENTER to confirm Save
        Run(adbExe, $"-s {dev} shell input keyevent 23")

        Threading.Thread.Sleep(3000)

        Console.WriteLine("Backup location selected and save triggered.")
        Console.WriteLine("Backup command sent.")

    End Sub

    Private Shared Function WaitForBackupFile(folder As String) As String

        Dim timeout = DateTime.Now.AddSeconds(60)

        Dim lastFile As String = Nothing

        While DateTime.Now < timeout

            Dim files = IO.Directory.GetFiles(folder, "*.tmb")

            If files.Length > 0 Then

                Dim newest = files.
                OrderByDescending(Function(f) IO.File.GetLastWriteTime(f)).
                First()

                If newest <> lastFile Then
                    lastFile = newest
                    Threading.Thread.Sleep(2000)

                    ' ensure file finished writing
                    Dim size1 = New IO.FileInfo(newest).Length
                    Threading.Thread.Sleep(1000)
                    Dim size2 = New IO.FileInfo(newest).Length

                    If size1 = size2 Then
                        Return newest
                    End If
                End If

            End If

            Threading.Thread.Sleep(500)

        End While

        Throw New Exception("Timed out waiting for backup file")

    End Function

    Private Shared Sub Run(exe As String, args As String)

        Dim p As New Process
        p.StartInfo.FileName = exe
        p.StartInfo.Arguments = args
        p.StartInfo.CreateNoWindow = True
        p.StartInfo.UseShellExecute = False
        p.StartInfo.RedirectStandardError = True
        p.Start()

        Dim err = p.StandardError.ReadToEnd()
        p.WaitForExit()

        If p.ExitCode <> 0 Then
            Console.WriteLine("ADB error: " & err)
        End If

    End Sub

End Class