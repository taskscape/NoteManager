using System.Text;

namespace NoteManager.App.UiTests.Infrastructure;

internal sealed class DisposableNoteVault : IDisposable
{
    private readonly string _temporaryBase;
    private readonly DateTime _baseTimestamp = DateTime.UtcNow;
    private bool _disposed;

    public DisposableNoteVault()
    {
        _temporaryBase = Path.GetFullPath(Path.GetTempPath());
        WorkspacePath = Path.Combine(
            _temporaryBase,
            $"NoteManager.UiTests.{Guid.NewGuid():N}");
        RootPath = Directory.CreateDirectory(
            Path.Combine(WorkspacePath, "vault")).FullName;
        OutsidePath = Directory.CreateDirectory(
            Path.Combine(WorkspacePath, "outside")).FullName;
        AlternateRootPath = Directory.CreateDirectory(
            Path.Combine(WorkspacePath, "alternate-vault")).FullName;
    }

    public string WorkspacePath { get; }
    public string RootPath { get; }
    public string OutsidePath { get; }
    public string AlternateRootPath { get; }
    public string CurrentNotePath { get; private set; } = string.Empty;
    public string NestedSearchNotePath { get; private set; } = string.Empty;
    public string MultiTagNotePath { get; private set; } = string.Empty;
    public string UntaggedNotePath { get; private set; } = string.Empty;
    public string MultiplePdfNotePath { get; private set; } = string.Empty;
    public string SecondEditableNotePath { get; private set; } = string.Empty;

    public void CreateStandardDataset()
    {
        var documentsFolder = Directory.CreateDirectory(
            Path.Combine(RootPath, "Documents")).FullName;
        File.Copy(
            UiTestPaths.SamplePdfPath,
            Path.Combine(documentsFolder, "guide.pdf"),
            overwrite: true);
        File.Copy(
            UiTestPaths.SamplePdfPath,
            Path.Combine(documentsFolder, "appendix.pdf"),
            overwrite: true);

        CurrentNotePath = WriteNote(
            "00 Current note.md",
            """
            # Current note

            This is the initially selected note.

            tags:
              - Alpha
              - Project.Demo

            ![[Documents/guide.pdf]]
            """,
            order: 0);
        NestedSearchNotePath = WriteNote(
            Path.Combine("Research", "01 Nested search.md"),
            """
            # Nested search

            The body-only regression signal is quantum-needle-9281.
            Folder discovery must search subfolders.

            tags:
              - Research
              - Beta-Tag
            """,
            order: 1);
        MultiTagNotePath = WriteNote(
            "02 Multiple tag blocks.md",
            """
            # Multiple tag blocks

            tags:
              - ALPHA
              - Shared

            Text between metadata.

            TAGS:
              - SECOND
              - shared
              - Release.2026
            """,
            order: 2);
        UntaggedNotePath = WriteNote(
            "03 Untagged.md",
            "# Untagged\n\nThis note deliberately has no tag block.",
            order: 3);
        MultiplePdfNotePath = WriteNote(
            "04 Multiple PDFs.md",
            """
            # Multiple PDFs

            tags:
              - documents

            ![[Documents/guide.pdf]]
            ![[Documents/appendix.pdf]]
            """,
            order: 4);
        WriteNote(
            "05 Unicode.md",
            """
            # Polecenia

            Zażółć gęślą jaźń.

            tags:
              - Szablon-Poleceń
              - Dot.Net
            """,
            order: 5);
        SecondEditableNotePath = WriteNote(
            Path.Combine("Archive", "06 Second editable.md"),
            "# Second editable\n\nOriginal second-note contents.",
            order: 6);
    }

    public void CreatePublishingDataset()
    {
        var assets = Directory.CreateDirectory(
            Path.Combine(RootPath, "assets")).FullName;
        File.WriteAllText(
            Path.Combine(assets, "sample.txt"),
            "embedded attachment payload",
            new UTF8Encoding(false));
        CurrentNotePath = WriteNote(
            "Published note.md",
            """
            # Published body

            ![[assets/sample.txt]]
            """,
            order: 0);
    }

    public void AddTagCatalog(int count)
    {
        for (var index = 0; index < count; index++)
        {
            WriteNote(
                Path.Combine(
                    "Catalog",
                    $"Catalog {index:00}.md"),
                $"""
                 # Catalog {index:00}

                 tags:
                   - Catalog-{index:00}
                 """,
                order: 20 + index);
        }
    }

    public string CreateAlternateDataset()
        => WriteNote(
            "Switched folder note.md",
            """
            # Switched folder note

            changed-folder-body-token

            tags:
              - switched
            """,
            order: 0,
            rootPath: AlternateRootPath);

    public string CreateExternalPdf(string fileName = "guide.pdf")
    {
        var path = Path.Combine(OutsidePath, fileName);
        File.Copy(UiTestPaths.SamplePdfPath, path, overwrite: true);
        return path;
    }

    public string WriteNote(
        string relativePath,
        string content,
        int order,
        string? rootPath = null)
    {
        var path = Path.Combine(rootPath ?? RootPath, relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Note directory is missing."));
        File.WriteAllText(
            path,
            NormalizeLineEndings(content),
            new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(
            path,
            _baseTimestamp.AddMinutes(-order));
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var fullWorkspace = Path.GetFullPath(WorkspacePath);
        if (!fullWorkspace.StartsWith(
                _temporaryBase,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullWorkspace).StartsWith(
                "NoteManager.UiTests.",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove an unexpected test path: {fullWorkspace}");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(fullWorkspace))
                {
                    Directory.Delete(fullWorkspace, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(150);
            }
        }
    }

    private static string NormalizeLineEndings(string content)
        => content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim()
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
}
