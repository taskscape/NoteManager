using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NoteManager.Desktop.Controls;

/// <summary>
/// Displays a related document that has no inline renderer and delegates
/// opening it to the operating system's registered application.
/// </summary>
public partial class DocumentAttachmentViewer : UserControl
{
    public static readonly StyledProperty<string?> DocumentPathProperty =
        AvaloniaProperty.Register<DocumentAttachmentViewer, string?>(
            nameof(DocumentPath));

    public DocumentAttachmentViewer()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Identifies the automatically discovered sibling document represented by
    /// this attachment card.
    /// </summary>
    public string? DocumentPath
    {
        get => GetValue(DocumentPathProperty);
        set => SetValue(DocumentPathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentPathProperty)
        {
            ShowDocument(change.NewValue as string);
        }
    }

    /// <summary>
    /// Shows enough file metadata to make an otherwise non-previewable
    /// attachment identifiable before the user opens it externally.
    /// </summary>
    private void ShowDocument(string? path)
    {
        FileNameText.Text = Path.GetFileName(path);
        // A control can receive a null path while bindings are changing, so use
        // an empty extension until a concrete related document is assigned.
        var extension = Path.GetExtension(path)?.TrimStart('.') ?? string.Empty;
        FileTypeText.Text = extension.Length == 0
            ? "Related document"
            : $"{extension.ToUpperInvariant()} document";
    }

    /// <summary>
    /// Uses Avalonia's platform launcher so related files open consistently in
    /// their default application on every supported desktop platform.
    /// </summary>
    private async void OpenExternally_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DocumentPath) || !File.Exists(DocumentPath))
        {
            FileTypeText.Text = $"Document not found: {DocumentPath}";
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null
            || !await launcher.LaunchUriAsync(new Uri(Path.GetFullPath(DocumentPath))))
        {
            FileTypeText.Text = "The operating system could not open this document";
        }
    }
}
