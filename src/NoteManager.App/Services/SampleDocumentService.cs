using System.IO.Compression;
using System.Text;
using NoteManager.App.Models;

namespace NoteManager.App.Services;

public static class SampleDocumentService
{
    private static readonly string DocumentsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NorthstarNoteManager",
        "SampleDocuments");

    public static void EnsureAll(IEnumerable<NoteItem> notes)
    {
        Directory.CreateDirectory(DocumentsFolder);

        foreach (var note in notes)
        {
            try
            {
                note.GeneratedFilePath = EnsureDocument(note);
            }
            catch
            {
                note.GeneratedFilePath = string.Empty;
            }
        }
    }

    public static string EnsureDocument(NoteItem note)
    {
        Directory.CreateDirectory(DocumentsFolder);
        var safeName = string.Concat(note.FileName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = Path.Combine(DocumentsFolder, safeName);

        if (File.Exists(path))
        {
            return path;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".docx")
        {
            WriteDocx(path, note);
        }
        else
        {
            WritePdf(path, note);
        }

        return path;
    }

    private static void WriteDocx(string path, NoteItem note)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);

        var body = new StringBuilder();
        body.Append(ParagraphXml("NORTHSTAR LABS", bold: true, size: 34));
        body.Append(ParagraphXml(note.DocumentHeading, bold: true, size: 28));
        body.Append(ParagraphXml(note.DocumentSubheading, bold: false, size: 22));
        foreach (var paragraph in note.Paragraphs)
        {
            body.Append(ParagraphXml(paragraph, bold: false, size: 22));
        }

        var documentXml =
            $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                {{body}}
                <w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr>
              </w:body>
            </w:document>
            """;
        WriteEntry(archive, "word/document.xml", documentXml);
    }

    private static string ParagraphXml(string text, bool bold, int size)
    {
        var escaped = SecurityElementEscape(text);
        var boldXml = bold ? "<w:b/>" : string.Empty;
        return $"<w:p><w:pPr><w:spacing w:after=\"180\"/></w:pPr><w:r><w:rPr>{boldXml}<w:sz w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{escaped}</w:t></w:r></w:p>";
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WritePdf(string path, NoteItem note)
    {
        var lines = new List<string>
        {
            "NORTHSTAR LABS",
            note.DocumentHeading,
            note.DocumentSubheading,
            string.Empty
        };

        foreach (var paragraph in note.Paragraphs)
        {
            lines.AddRange(Wrap(paragraph, 78));
            lines.Add(string.Empty);
        }

        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 15 Tf");
        content.AppendLine("72 760 Td");
        foreach (var line in lines.Take(34))
        {
            content.Append('(').Append(EscapePdf(line)).AppendLine(") Tj");
            content.AppendLine("0 -22 Td");
        }
        content.AppendLine("ET");

        var streamText = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(streamText)} >>\nstream\n{streamText}endstream"
        };

        using var memory = new MemoryStream();
        using var writer = new StreamWriter(memory, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        var offsets = new List<long> { 0 };

        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(memory.Position);
            writer.WriteLine($"{index + 1} 0 obj");
            writer.WriteLine(objects[index]);
            writer.WriteLine("endobj");
            writer.Flush();
        }

        var xrefOffset = memory.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objects.Length + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var offset in offsets.Skip(1))
        {
            writer.WriteLine($"{offset:0000000000} 00000 n ");
        }
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {objects.Length + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefOffset);
        writer.WriteLine("%%EOF");
        writer.Flush();

        File.WriteAllBytes(path, memory.ToArray());
    }

    private static IEnumerable<string> Wrap(string text, int length)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > length)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }
            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static string EscapePdf(string text)
        => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string SecurityElementEscape(string text)
        => System.Security.SecurityElement.Escape(text) ?? string.Empty;
}
