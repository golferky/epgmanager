Imports Microsoft.Data.Sqlite
Imports System.Xml.Linq
Imports System.Globalization
Imports System.Text.RegularExpressions
Imports System.IO
Imports System.Xml
Public Module GuideImporter

    Private Function Norm(t As String) As String
        If String.IsNullOrWhiteSpace(t) Then Return ""
        t = t.ToLowerInvariant()
        t = Regex.Replace(t, "\(\d{4}\)", "")
        t = Regex.Replace(t, "[^\w\s]", "")
        Return Regex.Replace(t, "\s+", " ").Trim()
    End Function
    Public Sub ImportXml(xmlFile As String, dbPath As String)

        Using con As New SqliteConnection($"Data Source={dbPath};Pooling=False;")
            con.Open()

            ' SQLite speed optimizations for bulk insert
            Using pragma As New SQLiteCommand("
            PRAGMA synchronous=OFF;
            PRAGMA journal_mode=MEMORY;
            PRAGMA temp_store=MEMORY;
        ", con)
                pragma.ExecuteNonQuery()
            End Using

            Using trans = con.BeginTransaction()

                Dim sql =
"INSERT OR IGNORE INTO guide
(title, normalized_title, channel, start_utc, end_utc, xml_file)
VALUES (@t,@n,@c,@s,@e,@x)"

                Using cmd As New SQLiteCommand(sql, con, trans)

                    cmd.Parameters.Add("@t", SqliteType.Text)
                    cmd.Parameters.Add("@n", SqliteType.Text)
                    cmd.Parameters.Add("@c", SqliteType.Text)
                    cmd.Parameters.Add("@s", SqliteType.Text)
                    cmd.Parameters.Add("@e", SqliteType.Text)
                    cmd.Parameters.Add("@x", SqliteType.Text)

                    Dim settings As New XmlReaderSettings()
                    settings.DtdProcessing = DtdProcessing.Parse

                    Using reader = XmlReader.Create(xmlFile, settings)
                        While reader.Read()

                            If reader.NodeType = XmlNodeType.Element AndAlso reader.Name = "programme" Then

                                Dim channel = reader.GetAttribute("channel")
                                Dim startAttr = reader.GetAttribute("start")
                                Dim stopAttr = reader.GetAttribute("stop")

                                If channel Is Nothing OrElse startAttr Is Nothing OrElse stopAttr Is Nothing Then Continue While

                                Dim startUtc = ParseXmltvTime(startAttr.Substring(0, 14))
                                Dim endUtc = ParseXmltvTime(stopAttr.Substring(0, 14))

                                Dim title As String = ""

                                While reader.Read()

                                    If reader.NodeType = XmlNodeType.Element AndAlso reader.Name = "title" Then
                                        title = reader.ReadElementContentAsString()
                                        Exit While
                                    End If

                                    If reader.NodeType = XmlNodeType.EndElement AndAlso reader.Name = "programme" Then
                                        Exit While
                                    End If

                                End While

                                If String.IsNullOrWhiteSpace(title) Then Continue While

                                Dim normalized = NormalizeTitle(title)

                                cmd.Parameters("@t").Value = title
                                cmd.Parameters("@n").Value = normalized
                                cmd.Parameters("@c").Value = channel
                                cmd.Parameters("@s").Value = startUtc
                                cmd.Parameters("@e").Value = endUtc
                                cmd.Parameters("@x").Value = Path.GetFileName(xmlFile)

                                cmd.ExecuteNonQuery()

                            End If

                        End While

                    End Using

                End Using

                trans.Commit()

            End Using

        End Using

    End Sub
    Private Sub EnsureIndexes(con As SqliteConnection)

        Using cmd As New SQLiteCommand("
            CREATE UNIQUE INDEX IF NOT EXISTS idx_guide_unique
            ON guide(channel, start_utc);
        ", con)

            cmd.ExecuteNonQuery()

        End Using

    End Sub
    Private Function ParseXmltvTime(xmlTime As String) As String

        ' XMLTV examples:
        ' 20260310184000
        ' 20260310184000 +0000
        ' 20260310184000 -0400

        Dim dt As DateTime

        If xmlTime.Length > 14 Then

            ' Has timezone offset
            Dim dto = DateTimeOffset.ParseExact(
            xmlTime,
            "yyyyMMddHHmmss zzz",
            CultureInfo.InvariantCulture)

            ' Convert to LOCAL time
            dt = dto.LocalDateTime

        Else

            ' No timezone → assume LOCAL already
            dt = DateTime.ParseExact(
            xmlTime,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal)

        End If

        Return dt.ToString("yyyyMMddHHmmss")

    End Function
End Module