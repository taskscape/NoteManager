namespace NoteManager.App.Services;

/// <summary>
/// Selects a path comparer from the semantics of the folder's actual volume.
/// </summary>
public static class FileSystemPathIdentity
{
    /// <summary>
    /// Uses a short-lived probe instead of operating-system heuristics because
    /// macOS and mounted volumes can be either case-sensitive or insensitive.
    /// </summary>
    public static StringComparer GetComparer(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var rootPath = Path.GetFullPath(folderPath);
        var probeName = $"notemanager-case-probe-{Guid.NewGuid():N}.tmp";
        var probePath = Path.Combine(rootPath, probeName);
        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            var alternateCasePath = Path.Combine(rootPath, probeName.ToUpperInvariant());
            // A differently cased spelling resolving to this probe proves that this volume merges path identities.
            return File.Exists(alternateCasePath)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            // Prefer the non-merging fallback when probing is unavailable so distinct paths are never collapsed by guesswork.
            return StringComparer.Ordinal;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                // The probe is best-effort cleanup; a later index run can safely continue if it cannot be removed now.
            }
        }
    }

    /// <summary>
    /// Applies the supplied volume-aware comparer to prefix checks as well as maps and sets.
    /// </summary>
    public static bool StartsWith(string path, string prefix, StringComparer comparer)
        => path.StartsWith(
            prefix,
            comparer.Equals(StringComparer.OrdinalIgnoreCase)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
