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


    Public Sub CheckNasStorage(path As String)

        Dim freeBytes As Long
        Dim totalBytes As Long
        Dim totalFreeBytes As Long

        Dim ok = GetDiskFreeSpaceEx(path, freeBytes, totalBytes, totalFreeBytes)

        If Not ok Then

            Debug.Print("Storage check failed")
            Debug.Print("Win32 Error → " & Marshal.GetLastWin32Error())
            Return

        End If


        Dim freeGB = freeBytes / 1024 / 1024 / 1024
        Dim totalGB = totalBytes / 1024 / 1024 / 1024

        Debug.Print("Path → " & path)
        Debug.Print("Total GB → " & Math.Round(totalGB, 2))
        Debug.Print("Free GB → " & Math.Round(freeGB, 2))

    End Sub

End Module