using System.IO;
using NoteManager.App.Infrastructure;

namespace NoteManager.App.Models;

public enum ThumbnailKind
{
    Globe,
    ShippingLabel,
    LegalLetter,
    Invoice,
    Table,
    SignedLetter,
    SpacePoster,
    Receipt,
    Report,
    Markdown
}

public sealed class NoteItem : ObservableObject
{
    private string _title = string.Empty;
    private string _plainTextContent = string.Empty;
    private string[] _pdfReferences = [];
    private string[] _tags = [];
    private bool _isDirty;

    public required string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public required string Subtitle { get; init; }
    public required string FileName { get; init; }
    public required string Size { get; init; }
    public required string Date { get; init; }
    public required string Notebook { get; init; }
    public required ThumbnailKind ThumbnailKind { get; init; }
    public required string DocumentHeading { get; init; }
    public required string DocumentSubheading { get; init; }
    public required string[] Paragraphs { get; init; }
    public required string[] Tags
    {
        get => _tags;
        init => _tags = value ?? [];
    }
    public string AttachmentDescription { get; init; } = "1 attachment";
    public string ModifiedAt { get; init; } = "26.07.2026 14:18";
    public string GeneratedFilePath { get; set; } = string.Empty;
    public bool IsMarkdownFile { get; init; }
    public string SourceFilePath { get; init; } = string.Empty;
    public string PlainTextContent
    {
        get => _plainTextContent;
        set
        {
            if (SetProperty(ref _plainTextContent, value))
            {
                IsDirty = true;
            }
        }
    }

    public bool IsContentLoaded { get; set; }
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public void LoadPlainTextContent(string content)
    {
        if (!_plainTextContent.Equals(content, StringComparison.Ordinal))
        {
            _plainTextContent = content;
            OnPropertyChanged(nameof(PlainTextContent));
        }

        IsContentLoaded = true;
        IsDirty = false;
    }

    public void MarkSaved() => IsDirty = false;

    public void ReplaceTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var updatedTags = tags
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (_tags.SequenceEqual(updatedTags, StringComparer.Ordinal))
        {
            return;
        }

        _tags = updatedTags;
        OnPropertyChanged(nameof(Tags));
    }

    public string[] PdfReferences
    {
        get => _pdfReferences;
        init => _pdfReferences = value ?? [];
    }

    public void AddPdfReferences(IEnumerable<string> paths)
    {
        var updatedReferences = _pdfReferences
            .Concat(paths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_pdfReferences.SequenceEqual(
            updatedReferences,
            StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _pdfReferences = updatedReferences;
        OnPropertyChanged(nameof(PdfReferences));
    }

    public bool IsWordDocument => string.Equals(Path.GetExtension(FileName), ".docx", StringComparison.OrdinalIgnoreCase);
    public string AttachmentGlyph => IsWordDocument ? "W" : "PDF";
    public string ListAttachmentText => IsMarkdownFile
        ? FileName
        : AttachmentDescription == "1 attachment"
        ? FileName
        : AttachmentDescription;
}
