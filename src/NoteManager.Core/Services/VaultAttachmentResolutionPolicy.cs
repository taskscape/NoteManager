namespace NoteManager.App.Services;

/// <summary>
/// Describes the vault-safe result of resolving an Obsidian attachment target.
/// </summary>
public sealed record VaultAttachmentResolution(
    string? ResolvedPath,
    string? Issue)
{
    /// <summary>
    /// Indicates whether the target resolved to one file inside the active vault.
    /// </summary>
    public bool IsResolved => ResolvedPath is not null;
}

/// <summary>
/// Applies one resolution policy to embedded-media preview and publication so a
/// visible attachment is never selected differently when it is shared.
/// </summary>
public static class VaultAttachmentResolutionPolicy
{
    /// <summary>
    /// Resolves explicit paths deterministically and bare filenames only when
    /// their vault-wide match is unique, without permitting outside-vault files.
    /// </summary>
    public static VaultAttachmentResolution Resolve(
        string literalTarget,
        string markdownFilePath,
        string vaultRoot,
        IEnumerable<string> vaultFilePaths,
        StringComparer? pathComparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(literalTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(vaultFilePaths);

        var target = literalTarget.Trim();
        var rootPath = Path.GetFullPath(vaultRoot);
        // Candidate paths represent files, unlike Markdown text, so resolve them with the vault volume's identity rules.
        var vaultPathComparer = pathComparer ?? FileSystemPathIdentity.GetComparer(rootPath);
        var notePath = Path.GetFullPath(markdownFilePath);
        var noteFolder = Path.GetDirectoryName(notePath)!;
        var windowsTarget = target.Replace('/', Path.DirectorySeparatorChar);
        var isExplicitRelative = target.StartsWith("./", StringComparison.Ordinal)
                                 || target.StartsWith("../", StringComparison.Ordinal)
                                 || target.StartsWith(@".\", StringComparison.Ordinal)
                                 || target.StartsWith(@"..\", StringComparison.Ordinal);
        var hasFolder = target.Contains('/') || target.Contains('\\');

        if (Path.IsPathRooted(windowsTarget))
        {
            // Absolute embeds may be previewed only when they still point into the current vault.
            return ResolveCandidate(Path.GetFullPath(windowsTarget), rootPath);
        }

        if (isExplicitRelative)
        {
            // ./ and ../ are anchored to the Markdown file rather than the vault root.
            return ResolveCandidate(
                Path.GetFullPath(Path.Combine(noteFolder, windowsTarget)),
                rootPath);
        }

        if (hasFolder)
        {
            // Folder-qualified links are intentionally vault-relative, matching Obsidian-style links.
            return ResolveCandidate(
                Path.GetFullPath(Path.Combine(rootPath, windowsTarget)),
                rootPath);
        }

        var noteRelative = Path.GetFullPath(Path.Combine(noteFolder, windowsTarget));
        if (IsPathInsideRoot(noteRelative, rootPath) && File.Exists(noteRelative))
        {
            return new VaultAttachmentResolution(noteRelative, Issue: null);
        }

        var rootRelative = Path.GetFullPath(Path.Combine(rootPath, windowsTarget));
        if (File.Exists(rootRelative))
        {
            return new VaultAttachmentResolution(rootRelative, Issue: null);
        }

        var matches = vaultFilePaths
            .Select(Path.GetFullPath)
            .Where(path => IsPathInsideRoot(path, rootPath))
            .Where(path => Path.GetFileName(path).Equals(
                Path.GetFileName(windowsTarget),
                vaultPathComparer.Equals(StringComparer.OrdinalIgnoreCase)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            .Distinct(vaultPathComparer)
            .OrderBy(path => path, vaultPathComparer)
            .ToArray();

        return matches.Length switch
        {
            1 => new VaultAttachmentResolution(matches[0], Issue: null),
            > 1 => new VaultAttachmentResolution(
                ResolvedPath: null,
                "filename is ambiguous in the current folder; use an explicit vault-relative or note-relative path"),
            _ => new VaultAttachmentResolution(
                ResolvedPath: null,
                "referenced in Markdown but not found inside the current folder")
        };
    }

    private static VaultAttachmentResolution ResolveCandidate(string candidate, string rootPath)
    {
        if (!IsPathInsideRoot(candidate, rootPath))
        {
            // Keep the rejected path out of preview and publishing rather than exposing a local external file.
            return new VaultAttachmentResolution(
                ResolvedPath: null,
                "points outside the current folder; move it into the folder or use a vault-relative path");
        }

        return File.Exists(candidate)
            ? new VaultAttachmentResolution(candidate, Issue: null)
            : new VaultAttachmentResolution(
                ResolvedPath: null,
                "referenced in Markdown but not found inside the current folder");
    }

    private static bool IsPathInsideRoot(string path, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        return !Path.IsPathRooted(relativePath)
               && !relativePath.Equals("..", StringComparison.Ordinal)
               && !relativePath.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }
}
