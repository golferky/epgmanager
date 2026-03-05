Imports System.IO
Imports System.Text.Json
Imports System.Runtime.InteropServices

Public Class ConfigManager

    Private ReadOnly _raw As Dictionary(Of String, JsonElement)

    Public Sub New(configPath As String)

        If Not File.Exists(configPath) Then
            Throw New Exception("Config file not found: " & configPath)
        End If

        Dim json = File.ReadAllText(configPath)

        _raw = JsonSerializer.Deserialize(Of Dictionary(Of String, JsonElement))(
            json,
            New JsonSerializerOptions With {
                .PropertyNameCaseInsensitive = True
            })

    End Sub


    ' =====================================
    ' BASIC GETTERS (STRONGLY SAFE)
    ' =====================================

    Public Function GetString(key As String, Optional defaultValue As String = "") As String

        If Not _raw.ContainsKey(key) Then Return defaultValue

        Dim val = _raw(key)

        If val.ValueKind = JsonValueKind.String Then
            Return val.GetString()
        End If

        Return val.ToString()

    End Function


    Public Function GetInt(key As String, Optional defaultValue As Integer = 0) As Integer

        If Not _raw.ContainsKey(key) Then Return defaultValue

        Dim result As Integer
        If Integer.TryParse(GetString(key), result) Then Return result

        Return defaultValue

    End Function


    Public Function GetBool(key As String, Optional defaultValue As Boolean = False) As Boolean

        If Not _raw.ContainsKey(key) Then Return defaultValue

        Dim result As Boolean
        If Boolean.TryParse(GetString(key), result) Then Return result

        Return defaultValue

    End Function


    ' =====================================
    ' PATH HANDLING (CROSS PLATFORM)
    ' =====================================

    Public Function GetPath(key As String) As String

        Dim rawPath = GetString(key)

        If String.IsNullOrWhiteSpace(rawPath) Then Return rawPath

        rawPath = rawPath.Trim().TrimEnd("/"c)

        If RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then
            Return ConvertMacToWindows(rawPath)
        Else
            Return rawPath
        End If

    End Function


    Private Function ConvertMacToWindows(macPath As String) As String

        If Not macPath.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase) Then
            Return macPath
        End If

        ' Remove /Volumes/
        Dim withoutVolumes = macPath.Substring(9)

        Dim parts = withoutVolumes.Split("/"c)

        If parts.Length = 0 Then Return macPath

        Dim shareName = parts(0).Trim()
        Dim remaining = String.Join("\", parts.Skip(1))

        Dim nas = GetString("MY_NAS_IP")

        If String.IsNullOrWhiteSpace(nas) Then
            Throw New Exception("MY_NAS_IP missing in config")
        End If

        If remaining.Length > 0 Then
            Return $"\\{nas}\{shareName}\{remaining}"
        Else
            Return $"\\{nas}\{shareName}"
        End If

    End Function


    ' =====================================
    ' SPECIAL HELPERS
    ' =====================================

    Public Function GetAdbPath() As String

        If RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then
            Return GetString("ADB_WIN_PATH")
        Else
            Return GetString("ADB_MAC_PATH")
        End If

    End Function


    Public Function GetNicknames() As Dictionary(Of String, String)

        If Not _raw.ContainsKey("NICKNAMES") Then
            Return New Dictionary(Of String, String)
        End If

        Return JsonSerializer.Deserialize(Of Dictionary(Of String, String))(
            _raw("NICKNAMES").GetRawText())

    End Function


    ' =====================================
    ' DEBUG
    ' =====================================

    Public Sub PrintSummary()

        Console.WriteLine("CONFIG SUMMARY")
        Console.WriteLine("--------------------------")

        For Each kv In _raw
            Console.WriteLine($"{kv.Key} = {kv.Value}")
        Next

    End Sub

End Class