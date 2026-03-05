Imports System.IO
Imports System.Text.RegularExpressions

Public Module PlexLibrary

    Private _plexPath As String

    Public Sub Init(config As ConfigManager)

        _plexPath = config.GetString("PLEX_MOVIES_PATH")

        If String.IsNullOrWhiteSpace(_plexPath) Then
            Throw New Exception("PLEX_MOVIES_PATH missing in config")
        End If

    End Sub


    Public Function MovieExists(title As String) As Boolean

        Dim clean = Normalize(title)

        For Each file In Directory.GetFiles(_plexPath, "*.mp4")

            Dim name = Path.GetFileNameWithoutExtension(file)

            If Normalize(name).Contains(clean) Then
                Return True
            End If

        Next

        Return False

    End Function


    Private Function Normalize(t As String) As String

        t = t.ToLowerInvariant()

        t = Regex.Replace(t, "\(\d{4}\)", "")
        t = Regex.Replace(t, "[^\w\s]", "")

        Return Regex.Replace(t, "\s+", " ").Trim()

    End Function

End Module