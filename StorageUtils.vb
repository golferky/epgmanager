Imports System.Runtime.InteropServices

Public Module StorageUtils

    Public Structure StorageStats
        Public Property Path As String
        Public Property FreeGB As Double
        Public Property TotalGB As Double
        Public ReadOnly Property UsedGB As Double
            Get
                Return TotalGB - FreeGB
            End Get
        End Property
        Public ReadOnly Property UsedPercent As Double
            Get
                If TotalGB <= 0 Then Return 0
                Return (UsedGB / TotalGB) * 100.0
            End Get
        End Property
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function GetDiskFreeSpaceEx(
        lpDirectoryName As String,
        ByRef lpFreeBytesAvailable As Long,
        ByRef lpTotalNumberOfBytes As Long,
        ByRef lpTotalNumberOfFreeBytes As Long
    ) As Boolean
    End Function


    Public Function CheckNasStorage(path As String) As Double
        Dim stats = CheckStorageStats(path)
        Return stats.FreeGB
    End Function

    Public Function CheckStorageStats(path As String) As StorageStats
        Dim freeBytes As Long
        Dim totalBytes As Long
        Dim totalFreeBytes As Long
        Dim ok = GetDiskFreeSpaceEx(path, freeBytes, totalBytes, totalFreeBytes)
        If Not ok Then
            Debug.Print("Storage check failed")
            Debug.Print("Win32 Error → " & Marshal.GetLastWin32Error())
            Return New StorageStats With {
                .Path = path,
                .FreeGB = -1,
                .TotalGB = -1
            }
        End If
        Dim freeGB = freeBytes / 1024.0 / 1024.0 / 1024.0
        Dim totalGB = totalBytes / 1024.0 / 1024.0 / 1024.0

        Return New StorageStats With {
            .Path = path,
            .FreeGB = freeGB,
            .TotalGB = totalGB
        }
    End Function

End Module
