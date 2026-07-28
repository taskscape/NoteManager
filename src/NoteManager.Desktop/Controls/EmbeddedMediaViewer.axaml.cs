using Avalonia;
using Avalonia.Controls;
using NoteManager.App.Models;

namespace NoteManager.Desktop.Controls;

public partial class EmbeddedMediaViewer : UserControl
{
    public static readonly StyledProperty<EmbeddedMediaReference?> MediaProperty =
        AvaloniaProperty.Register<EmbeddedMediaViewer, EmbeddedMediaReference?>(
            nameof(Media));

    public EmbeddedMediaViewer()
    {
        InitializeComponent();
    }

    public EmbeddedMediaReference? Media
    {
        get => GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaProperty)
        {
            ShowMedia(change.NewValue as EmbeddedMediaReference);
        }
    }

    private void ShowMedia(EmbeddedMediaReference? media)
    {
        ViewerHost.Content = media switch
        {
            { Kind: EmbeddedMediaKind.Pdf } => new PdfViewer
            {
                Height = 560,
                PdfPath = media.ResolvedPath
            },
            { Kind: EmbeddedMediaKind.Image } => new ImageViewer
            {
                ImagePath = media.ResolvedPath
            },
            _ => null
        };
    }
}
