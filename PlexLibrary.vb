Imports System.IO
Imports System.Text.RegularExpressions

Public Module PlexLibrary


    Public Function MovieExists(title As String) As Boolean

        Dim clean = Normalize(title)
        Console.WriteLine("DEBUG PLEX PATH=[" & _plexMoviesPath & "]")

        For Each file In Directory.GetFiles(_plexMoviesPath, "*.mp4")

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