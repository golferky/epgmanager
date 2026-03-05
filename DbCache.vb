Imports System.IO

Public Module DbCache

    Private _map As New Dictionary(Of String, String)
    Private _locals As New List(Of String)

    Public Function GetLocalCopy(networkPath As String) As String

        ' Reuse if already copied
        If _map.ContainsValue(networkPath) Then
            Return _map.First(Function(x) x.Value = networkPath).Key
        End If

        ' Unique temp filename per run (prevents collision)
        Dim local = Path.Combine(
            Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(networkPath) &
            "_" & Guid.NewGuid().ToString("N") &
            Path.GetExtension(networkPath)
        )

        File.Copy(networkPath, local, True)

        _map(local) = networkPath
        _locals.Add(local)

        Return local

    End Function


    Public Sub Commit(localPath As String)

        If Not _map.ContainsKey(localPath) Then Exit Sub

        Dim network = _map(localPath)

        ' Ensure SQLite connections are closed before commit
        GC.Collect()
        GC.WaitForPendingFinalizers()

        ' Use temp swap to prevent corruption
        Dim tempNetwork = network & ".tmp"

        File.Copy(localPath, tempNetwork, True)

        If File.Exists(network) Then
            File.Delete(network)
        End If

        File.Move(tempNetwork, network)

    End Sub


    Public Sub Cleanup()

        For Each localfile In _locals
            Try
                If File.Exists(localfile) Then
                    File.Delete(localfile)
                End If
            Catch
            End Try
        Next

        _locals.Clear()
        _map.Clear()

    End Sub

End Module