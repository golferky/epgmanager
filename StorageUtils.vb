Imports System.Runtime.InteropServices

Public Module StorageUtils

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function GetDiskFreeSpaceEx(
        lpDirectoryName As String,
        ByRef lpFreeBytesAvailable As Long,
        ByRef lpTotalNumberOfBytes As Long,
        ByRef lpTotalNumberOfFreeBytes As Long
    ) As Boolean
    End Function


    Public Function CheckNasStorage(path As String) As Double
        Dim freeBytes As Long
        Dim totalBytes As Long
        Dim totalFreeBytes As Long
        Dim ok = GetDiskFreeSpaceEx(path, freeBytes, totalBytes, totalFreeBytes)
        If Not ok Then
            Debug.Print("Storage check failed")
            Debug.Print("Win32 Error → " & Marshal.GetLastWin32Error())
            Return -1
        End If
        Dim freeGB = freeBytes / 1024.0 / 1024.0 / 1024.0
        Dim totalGB = totalBytes / 1024.0 / 1024.0 / 1024.0

        Return freeGB
    End Function

End Module