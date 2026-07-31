using System.Text;

namespace NoteManager.Desktop.UiTests.Infrastructure;

internal sealed class SearchTestVault : IDisposable
{
    private readonly string _temporaryRoot;
    private readonly DateTime _baseTimestamp = DateTime.UtcNow;
    private bool _disposed;

    public SearchTestVault()
    {
        _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        WorkspacePath = Path.Combine(
            _temporaryRoot,
            $"NoteManager.SearchUiTests.{Guid.NewGuid():N}");
        RootPath = Directory.CreateDirectory(
            Path.Combine(WorkspacePath, "vault")).FullName;
    }

    public string WorkspacePath { get; }
    public string RootPath { get; }

    public void CreateDataset()
    {
        WriteNote(
            "01 Project plan.md",
            """
            # Project plan

            The quarterly project plan is approved.
            The literal reference is docs/search.md.

            tags:
              - active
              - planning
            """,
            modifiedMinutesAgo: 2);
        WriteNote(
            "02 Project planning.md",
            """
            # Project planning

            Project planning contains the beta implementation signal.

            tags:
              - active
            """,
            modifiedMinutesAgo: 1);
        WriteNote(
            "03 Invoice.md",
            """
            # Invoice

            This invoice is paid.

            tags:
              - finance
            """,
            modifiedMinutesAgo: 4);
        WriteNote(
            "04 Draft invoice.md",
            """
            # Draft invoice

            This invoice remains a draft.

            tags:
              - finance
            """,
            modifiedMinutesAgo: 3);
        WriteNote(
            Path.Combine("Archive", "05 Archived roadmap.md"),
            """
            # Archived roadmap

            This roadmap is archived.
            """,
            modifiedMinutesAgo: 5);
        WriteNote(
            "06 Roadmap.md",
            """
            # Roadmap

            The project roadmap covers delivery.
            """,
            modifiedMinutesAgo: 6);
        WriteNote(
            "07 Beta reference.md",
            """
            # Beta reference

            A beta-only reference with no related keyword.
            """,
            modifiedMinutesAgo: 7);
    }

    public void CreateAdditionalIndexingNotes(int count)
    {
        for (var index = 0; index < count; index++)
        {
            WriteNote(
                $"Indexing {index:D4}.md",
                $"# Indexing {index}\n\n{new string('x', 1_024)}",
                modifiedMinutesAgo: index + 10);
        }
    }

    private void WriteNote(
        string relativePath,
        string content,
        int modifiedMinutesAgo)
    {
        var path = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Note folder is missing."));
        File.WriteAllText(
            path,
            content.Trim().ReplaceLineEndings(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetLastWriteTimeUtc(
            path,
            _baseTimestamp.AddMinutes(-modifiedMinutesAgo));
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
                _temporaryRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullWorkspace).StartsWith(
                "NoteManager.SearchUiTests.",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove unexpected UI test path: {fullWorkspace}");
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
            catch (Exception exception) when (
                attempt < 4
                && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150);
            }
        }
    }
}
