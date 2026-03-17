Imports Microsoft.Data.Sqlite

Public Module ChannelTagger

    Public Sub TagChannels(dbPath As String)

        Using con As New SqliteConnection($"{dbPath};Pooling=False;")

            con.Open()

            Console.WriteLine("Tagging channels...")

            ' Reset types
            Execute(con, "UPDATE channels SET type=NULL")

            TagLocals(con)
            TagSports(con)
            TagNews(con)
            TagKids(con)
            TagMovies(con)
            TagDocumentary(con)
            TagInternational(con)
            TagEntertainment(con)

            Console.WriteLine("Channel tagging complete.")

        End Using

    End Sub


    Private Sub TagLocals(con As SqliteConnection)

        Console.WriteLine("Tagging locals...")

        Execute(con,
        "UPDATE channels SET type='locals'
            WHERE nickname LIKE '%Cincinnati OH%'")

        Execute(con,
        "UPDATE channels SET type='locals-abc'
         WHERE nickname LIKE '%Cincinnati OH%'
         AND LOWER(nickname) LIKE '%abc%'")

        Execute(con,
        "UPDATE channels SET type='locals-nbc'
         WHERE nickname LIKE '%Cincinnati OH%'
         AND LOWER(nickname) LIKE '%nbc%'")

        Execute(con,
        "UPDATE channels SET type='locals-cbs'
         WHERE nickname LIKE '%Cincinnati OH%'
         AND LOWER(nickname) LIKE '%cbs%'")

        Execute(con,
        "UPDATE channels SET type='locals-fox'
         WHERE nickname LIKE '%Cincinnati OH%'
         AND LOWER(nickname) LIKE '%fox%'")
        Execute(con,
        "UPDATE channels
         SET type='locals-cw'
         WHERE nickname LIKE '%Cincinnati OH%'
         AND LOWER(nickname) LIKE '%cw%';")

    End Sub


    Private Sub TagSports(con As SqliteConnection)

        Console.WriteLine("Tagging sports...")

        Execute(con,
        "UPDATE channels SET type='sports'
        WHERE LOWER(nickname) LIKE '%espn%'
         OR LOWER(nickname) LIKE '%fox sports%'
         OR LOWER(nickname) LIKE '%nbc sports%'
         OR LOWER(nickname) LIKE '%beinsport%'
         OR LOWER(nickname) LIKE '%golf%'
         OR LOWER(nickname) LIKE '%tennis%'
         OR LOWER(nickname) LIKE '%mlb%'
         OR LOWER(nickname) LIKE '%nba%'
         OR LOWER(nickname) LIKE '%nhl%'
         OR LOWER(nickname) LIKE '%nfl%'")

        Execute(con,
        "UPDATE channels
SET type='sports-college'
WHERE LOWER(nickname) LIKE '%pac-12%'
OR LOWER(nickname) LIKE '%sec%'
OR LOWER(nickname) LIKE '%big ten%'
OR LOWER(nickname) LIKE '%acc%';")

        Execute(con,
        "UPDATE channels
SET type='sports-pay'
WHERE LOWER(nickname) LIKE '%bally sports%'
;")

    End Sub


    Private Sub TagNews(con As SqliteConnection)

        Console.WriteLine("Tagging news...")

        Execute(con,
        "UPDATE channels SET type='news'
         WHERE LOWER(nickname) LIKE '%cnn%'
         OR LOWER(nickname) LIKE '%fox news%'
         OR LOWER(nickname) LIKE '%msnbc%'
         OR LOWER(nickname) LIKE '%newsmax%'
         OR LOWER(nickname) LIKE '%bbc news%'
         OR LOWER(nickname) LIKE '%sky news%'")

    End Sub


    Private Sub TagKids(con As SqliteConnection)

        Console.WriteLine("Tagging kids...")

        Execute(con,
        "UPDATE channels SET type='kids'
         WHERE LOWER(nickname) LIKE '%nick%'
         OR LOWER(nickname) LIKE '%cartoon%'
         OR LOWER(nickname) LIKE '%disney%'
         OR LOWER(nickname) LIKE '%boomerang%'
         OR LOWER(nickname) LIKE '%pbs kids%'")

    End Sub


    Private Sub TagMovies(con As SqliteConnection)

        Console.WriteLine("Tagging movie channels...")

        Execute(con,
        "UPDATE channels SET type='movies'
         WHERE LOWER(nickname) LIKE '%hbo%'
         OR LOWER(nickname) LIKE '%cinemax%'
         OR LOWER(nickname) LIKE '%starz%'
         OR LOWER(nickname) LIKE '%showtime%'
         OR LOWER(nickname) LIKE '%tcm%'
         OR LOWER(nickname) LIKE '%amc%'")

    End Sub


    Private Sub TagDocumentary(con As SqliteConnection)

        Console.WriteLine("Tagging documentary channels...")

        Execute(con,
        "UPDATE channels SET type='documentary'
         WHERE LOWER(nickname) LIKE '%history%'
         OR LOWER(nickname) LIKE '%discovery%'
         OR LOWER(nickname) LIKE '%nat geo%'
         OR LOWER(nickname) LIKE '%smithsonian%'")

    End Sub


    Private Sub TagInternational(con As SqliteConnection)

        Console.WriteLine("Tagging international channels...")

        Execute(con,
        "UPDATE channels SET type='international'
         WHERE LOWER(nickname) LIKE '%uk%'
         OR LOWER(nickname) LIKE '%canada%'
         OR LOWER(nickname) LIKE '%latin%'
         OR LOWER(nickname) LIKE '%spanish%'
         OR LOWER(nickname) LIKE '%mexico%'")

    End Sub
    Private Sub TagEntertainment(con As SqliteConnection)

        Console.WriteLine("Tagging entertainment channels...")

        Execute(con,
    "UPDATE channels
     SET type='entertainment'
     WHERE type IS NULL")

    End Sub

    Private Sub Execute(con As SqliteConnection, sql As String)

        Using cmd As New SqliteCommand(sql, con)
            cmd.ExecuteNonQuery()
        End Using

    End Sub

End Module