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
    private string _size = string.Empty;
    private string _modifiedAt = "26.07.2026 14:18";
    private string _plainTextContent = string.Empty;
    private EmbeddedMediaReference[] _embeddedMediaReferences = [];
    private string[] _tags = [];
    private DateTime _updatedAt;
    private long _sizeBytes;
    private bool _isDirty;

    public required string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public required string Subtitle { get; init; }
    public required string FileName { get; init; }
    public required string Size
    {
        get => _size;
        init => _size = value;
    }
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
    public string ModifiedAt
    {
        get => _modifiedAt;
        init => _modifiedAt = value;
    }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        init => _updatedAt = value;
    }
    public long SizeBytes
    {
        get => _sizeBytes;
        init => _sizeBytes = value;
    }
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

    public void UpdateFileMetadata(
        string size,
        long sizeBytes,
        string modifiedAt,
        DateTime updatedAt)
    {
        if (!_size.Equals(size, StringComparison.Ordinal))
        {
            _size = size;
            OnPropertyChanged(nameof(Size));
        }

        if (_sizeBytes != sizeBytes)
        {
            _sizeBytes = sizeBytes;
            OnPropertyChanged(nameof(SizeBytes));
        }

        if (!_modifiedAt.Equals(modifiedAt, StringComparison.Ordinal))
        {
            _modifiedAt = modifiedAt;
            OnPropertyChanged(nameof(ModifiedAt));
        }

        if (_updatedAt != updatedAt)
        {
            _updatedAt = updatedAt;
            OnPropertyChanged(nameof(UpdatedAt));
        }
    }

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

    public EmbeddedMediaReference[] EmbeddedMediaReferences
    {
        get => _embeddedMediaReferences;
        init => _embeddedMediaReferences = value ?? [];
    }

    public void AddEmbeddedMediaReferences(
        IEnumerable<EmbeddedMediaReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        var updatedReferences = _embeddedMediaReferences
            .Concat(references)
            .Where(reference => !string.IsNullOrWhiteSpace(reference.ResolvedPath))
            .ToArray();
        if (_embeddedMediaReferences.SequenceEqual(updatedReferences))
        {
            return;
        }

        _embeddedMediaReferences = updatedReferences;
        OnPropertyChanged(nameof(EmbeddedMediaReferences));
    }

    public void ReplaceEmbeddedMediaReferences(
        IEnumerable<EmbeddedMediaReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        var updatedReferences = references.ToArray();
        if (_embeddedMediaReferences.SequenceEqual(updatedReferences))
        {
            return;
        }

        _embeddedMediaReferences = updatedReferences;
        OnPropertyChanged(nameof(EmbeddedMediaReferences));
    }

    public bool IsWordDocument => string.Equals(Path.GetExtension(FileName), ".docx", StringComparison.OrdinalIgnoreCase);
    public string AttachmentGlyph => IsWordDocument ? "W" : "PDF";
    public string ListAttachmentText => IsMarkdownFile
        ? FileName
        : AttachmentDescription == "1 attachment"
        ? FileName
        : AttachmentDescription;
}
