namespace NoteManager.Plugin.GitIntegration;

public sealed record GitRepositoryState(
    bool CanSynchronize,
    string Message,
    string Branch = "",
    string Upstream = "",
    string GitDirectory = "");

public sealed class GitRepositoryInspector(GitProcessRunner runner)
{
    public async Task<GitRepositoryState> InspectAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var inside = await runner.RunAsync(
            repositoryPath,
            ["rev-parse", "--is-inside-work-tree"],
            cancellationToken);
        if (!inside.Succeeded
            || !inside.StandardOutput.Trim().Equals(
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return new GitRepositoryState(false, "The selected folder is not a Git work tree.");
        }

        var topLevel = await RequiredAsync(
            repositoryPath,
            ["rev-parse", "--show-toplevel"],
            "Git could not determine the work-tree root.",
            cancellationToken);
        if (!topLevel.Result.Succeeded)
        {
            return topLevel.State;
        }

        var selectedRoot = NormalizePath(repositoryPath);
        var detectedRoot = NormalizePath(topLevel.Result.StandardOutput.Trim());
        if (!PathEquals(selectedRoot, detectedRoot))
        {
            return new GitRepositoryState(
                false,
                "The selected folder is below a larger Git repository; synchronization was skipped.");
        }

        var branch = await RequiredAsync(
            repositoryPath,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            "The repository has a detached HEAD.",
            cancellationToken);
        if (!branch.Result.Succeeded)
        {
            return branch.State;
        }

        var upstream = await RequiredAsync(
            repositoryPath,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
            "The current branch has no configured upstream.",
            cancellationToken);
        if (!upstream.Result.Succeeded)
        {
            return upstream.State;
        }

        var gitDirectoryResult = await RequiredAsync(
            repositoryPath,
            ["rev-parse", "--absolute-git-dir"],
            "Git could not determine its metadata directory.",
            cancellationToken);
        if (!gitDirectoryResult.Result.Succeeded)
        {
            return gitDirectoryResult.State;
        }

        var gitDirectory = gitDirectoryResult.Result.StandardOutput.Trim();
        if (HasUnfinishedOperation(gitDirectory))
        {
            return new GitRepositoryState(
                false,
                "A merge, rebase, cherry-pick, or revert is already in progress.");
        }

        if (File.Exists(Path.Combine(gitDirectory, "index.lock")))
        {
            return new GitRepositoryState(
                false,
                "Another Git process owns the repository index lock.");
        }

        var conflicts = await runner.RunAsync(
            repositoryPath,
            ["diff", "--name-only", "--diff-filter=U", "-z"],
            cancellationToken);
        if (!conflicts.Succeeded || conflicts.StandardOutput.Length > 0)
        {
            return new GitRepositoryState(
                false,
                conflicts.StandardOutput.Length > 0
                    ? "The repository contains unresolved conflicts."
                    : "Git could not inspect the repository conflict state.");
        }

        foreach (var key in new[] { "user.name", "user.email" })
        {
            var identity = await runner.RunAsync(
                repositoryPath,
                ["config", "--get", key],
                cancellationToken);
            if (!identity.Succeeded || string.IsNullOrWhiteSpace(identity.StandardOutput))
            {
                return new GitRepositoryState(
                    false,
                    $"Git {key} is not configured for this repository.");
            }
        }

        return new GitRepositoryState(
            true,
            "Repository ready.",
            branch.Result.StandardOutput.Trim(),
            upstream.Result.StandardOutput.Trim(),
            gitDirectory);
    }

    private static bool HasUnfinishedOperation(string gitDirectory)
        => File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD"))
           || File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD"))
           || File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD"))
           || Directory.Exists(Path.Combine(gitDirectory, "rebase-merge"))
           || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply"));

    private async Task<(GitCommandResult Result, GitRepositoryState State)> RequiredAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            arguments,
            cancellationToken);
        return (
            result,
            result.Succeeded
                ? new GitRepositoryState(true, string.Empty)
                : new GitRepositoryState(false, failureMessage));
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathEquals(string left, string right)
        => left.Equals(
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
