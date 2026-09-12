namespace NoteManager.App.Services;

/// <summary>
/// Keeps literal attachment paths distinct from Obsidian's alias and fragment syntax.
/// </summary>
public static class ObsidianEmbedTarget
{
    /// <summary>
    /// Removes unescaped Obsidian syntax before decoding the literal path once.
    /// This ordering preserves percent-escaped filename characters such as <c>%23</c> and <c>%7C</c>.
    /// </summary>
    public static string GetLiteralPath(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var value = target.Trim().Trim('<', '>', '"', '\'');
        var syntaxIndex = value.IndexOfAny(['|', '#']);
        if (syntaxIndex >= 0)
        {
            // Only unescaped delimiters introduce markup; encoded delimiters remain part of the filename.
            value = value[..syntaxIndex];
        }

        return Uri.UnescapeDataString(value).Trim();
    }

    /// <summary>
    /// Escapes filename characters that Obsidian would otherwise treat as embed syntax.
    /// </summary>
    public static string EscapeLiteralPath(string relativePath) => relativePath
        .Replace(Path.DirectorySeparatorChar, '/')
        // Escape percent first so a literal percent-encoded-looking filename is decoded only once.
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("#", "%23", StringComparison.Ordinal)
        .Replace("|", "%7C", StringComparison.Ordinal)
        .Replace("[", "%5B", StringComparison.Ordinal)
        .Replace("]", "%5D", StringComparison.Ordinal);
}
