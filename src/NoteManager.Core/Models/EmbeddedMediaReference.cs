namespace NoteManager.App.Models;

public enum EmbeddedMediaKind
{
    Pdf,
    Image,

    /// <summary>
    /// Represents a related file that must be shown as an attachment because
    /// NoteManager does not render its contents inline.
    /// </summary>
    Document
}

public sealed record EmbeddedMediaReference(
    string Target,
    string ResolvedPath,
    EmbeddedMediaKind Kind)
{
    /// <summary>
    /// Provides the sibling file name for both inline media and automatically
    /// discovered related-document attachment cards.
    /// </summary>
    public string FileName => Path.GetFileName(ResolvedPath);

    public static bool TryGetKind(string path, out EmbeddedMediaKind kind)
    {
        switch (Path.GetExtension(path))
        {
            case var extension when extension.Equals(
                ".pdf",
                StringComparison.OrdinalIgnoreCase):
                kind = EmbeddedMediaKind.Pdf;
                return true;
            case var extension when extension.Equals(
                ".png",
                StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase):
                kind = EmbeddedMediaKind.Image;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
