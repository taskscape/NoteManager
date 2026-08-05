namespace NoteManager.App.Models;

public enum EmbeddedMediaKind
{
    Pdf,
    Image
}

public sealed record EmbeddedMediaReference(
    string Target,
    string ResolvedPath,
    EmbeddedMediaKind Kind)
{
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
