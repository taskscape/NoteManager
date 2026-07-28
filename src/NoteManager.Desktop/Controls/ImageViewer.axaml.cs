using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace NoteManager.Desktop.Controls;

public partial class ImageViewer : UserControl
{
    private const int PreviewDecodeWidth = 1920;
    private Bitmap? _bitmap;

    public static readonly StyledProperty<string?> ImagePathProperty =
        AvaloniaProperty.Register<ImageViewer, string?>(nameof(ImagePath));

    public ImageViewer()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => LoadImage(ImagePath);
        DetachedFromVisualTree += (_, _) => ClearImage();
    }

    public string? ImagePath
    {
        get => GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImagePathProperty)
        {
            LoadImage(change.NewValue as string);
        }
    }

    private void LoadImage(string? path)
    {
        ClearImage();
        FileNameText.Text = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = $"Image not found: {path}";
            return;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            _bitmap = Bitmap.DecodeToWidth(
                stream,
                PreviewDecodeWidth,
                BitmapInterpolationMode.HighQuality);
            PreviewImage.Source = _bitmap;
            StatusText.Text = $"Image ready · {_bitmap.PixelSize.Width:N0} × {_bitmap.PixelSize.Height:N0}";
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            StatusText.Text = $"Could not display image: {exception.Message}";
        }
    }

    private void ClearImage()
    {
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private async void OpenExternally_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath))
        {
            StatusText.Text = $"Image not found: {ImagePath}";
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null
            || !await launcher.LaunchUriAsync(new Uri(Path.GetFullPath(ImagePath))))
        {
            StatusText.Text = "The operating system could not open this image";
        }
    }
}
