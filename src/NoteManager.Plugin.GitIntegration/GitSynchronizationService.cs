using NoteManager.Plugins;

namespace NoteManager.Plugin.GitIntegration;

public sealed record GitSynchronizationResult(
    bool Succeeded,
    bool Skipped,
    string Message,
    int ChangedPathCount = 0);

public sealed class GitSynchronizationService(
    GitProcessRunner runner,
    GitSynchronizationLog log,
    string commitMessagePrefix = "NoteManager automatic sync")
{
    private readonly GitRepositoryInspector _inspector = new(runner);

    public async Task<GitSynchronizationResult> SynchronizeAsync(
        PluginHostContext context,
        CancellationToken cancellationToken = default)
    {
        FileStream synchronizationLock;
        try
        {
            Directory.CreateDirectory(context.ConfigurationDirectory);
            synchronizationLock = new FileStream(
                Path.Combine(context.ConfigurationDirectory, "synchronization.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            const string message =
                "Another NoteManager process is synchronizing this repository.";
            context.ReportStatus(message);
            return new GitSynchronizationResult(false, true, message);
        }

        await using var heldSynchronizationLock = synchronizationLock;
        var state = await _inspector.InspectAsync(context.VaultPath, cancellationToken);
        if (!state.CanSynchronize)
        {
            await LogAndReportAsync(context, $"Git synchronization skipped: {state.Message}", cancellationToken);
            return new GitSynchronizationResult(false, true, state.Message);
        }

        context.ReportStatus("Saving the active note before Git synchronization…");
        if (!await context.SaveActiveNoteAsync(cancellationToken))
        {
            const string message = "The active note could not be saved; Git synchronization stopped.";
            await LogAndReportAsync(context, message, cancellationToken);
            return new GitSynchronizationResult(false, false, message);
        }

        context.ReportStatus("Pulling remote Git changes…");
        var pull = await RunLoggedAsync(
            context.VaultPath,
            ["pull", "--rebase", "--autostash"],
            cancellationToken);
        if (!pull.Succeeded)
        {
            return await FailureAsync(context, "Git pull failed", pull, cancellationToken);
        }

        var refreshedState = await _inspector.InspectAsync(context.VaultPath, cancellationToken);
        if (!refreshedState.CanSynchronize)
        {
            await LogAndReportAsync(
                context,
                $"Git synchronization stopped after pull: {refreshedState.Message}",
                cancellationToken);
            return new GitSynchronizationResult(false, false, refreshedState.Message);
        }

        context.ReportStatus("Staging local Git changes…");
        var trackedPathsResult = await RunLoggedAsync(
            context.VaultPath,
            ["ls-files", "-z"],
            cancellationToken);
        if (!trackedPathsResult.Succeeded)
        {
            return await FailureAsync(
                context,
                "Git could not inspect tracked paths",
                trackedPathsResult,
                cancellationToken);
        }

        var trackedPaths = new HashSet<string>(
            SplitNullDelimited(trackedPathsResult.StandardOutput),
            StringComparer.Ordinal);
        var updateTracked = await RunLoggedAsync(
            context.VaultPath,
            ["add", "-u", "--", "."],
            cancellationToken);
        if (!updateTracked.Succeeded)
        {
            return await FailureAsync(
                context,
                "Git could not stage tracked path changes",
                updateTracked,
                cancellationToken);
        }

        var addUntracked = await RunLoggedAsync(
            context.VaultPath,
            [
                "add", "-A", "--", ".",
                ":(exclude,glob).*",
                ":(exclude,glob)**/.*",
                ":(exclude,glob).*/**",
                ":(exclude,glob)**/.*/**"
            ],
            cancellationToken);
        if (!addUntracked.Succeeded)
        {
            return await FailureAsync(
                context,
                "Git staging failed",
                addUntracked,
                cancellationToken);
        }

        var stagedPathsResult = await RunLoggedAsync(
            context.VaultPath,
            ["diff", "--cached", "--name-only", "-z"],
            cancellationToken);
        if (!stagedPathsResult.Succeeded)
        {
            return await FailureAsync(
                context,
                "Git could not validate staged paths",
                stagedPathsResult,
                cancellationToken);
        }

        var stagedPaths = SplitNullDelimited(stagedPathsResult.StandardOutput);
        var prohibitedPath = stagedPaths.FirstOrDefault(path =>
            ContainsDotPrefixedSegment(path) && !trackedPaths.Contains(path));
        if (prohibitedPath is not null)
        {
            var message =
                $"Git staging was stopped because untracked dot-prefixed path '{prohibitedPath}' was staged.";
            await LogAndReportAsync(context, message, cancellationToken);
            return new GitSynchronizationResult(false, false, message);
        }

        var hasChanges = await RunLoggedAsync(
            context.VaultPath,
            ["diff", "--cached", "--quiet"],
            cancellationToken);
        if (hasChanges.ExitCode is not (0 or 1)
            || hasChanges.WasCancelled
            || hasChanges.TimedOut)
        {
            return await FailureAsync(
                context,
                "Git could not inspect the staged index",
                hasChanges,
                cancellationToken);
        }

        if (hasChanges.ExitCode == 1)
        {
            context.ReportStatus($"Committing {stagedPaths.Count:N0} Git change(s)…");
            var message = $"{commitMessagePrefix} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}";
            var commit = await RunLoggedAsync(
                context.VaultPath,
                ["commit", "-m", message],
                cancellationToken);
            if (!commit.Succeeded)
            {
                return await FailureAsync(context, "Git commit failed", commit, cancellationToken);
            }
        }

        context.ReportStatus("Pushing Git changes…");
        var push = await RunLoggedAsync(
            context.VaultPath,
            ["push"],
            cancellationToken);
        if (!push.Succeeded)
        {
            return await FailureAsync(context, "Git push failed", push, cancellationToken);
        }

        var success = stagedPaths.Count == 0
            ? $"Git synchronization complete on {state.Branch}; no local changes were committed."
            : $"Git synchronization complete on {state.Branch}; {stagedPaths.Count:N0} path(s) committed.";
        await LogAndReportAsync(context, success, cancellationToken);
        return new GitSynchronizationResult(true, false, success, stagedPaths.Count);
    }

    private async Task<GitCommandResult> RunLoggedAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(repositoryPath, arguments, cancellationToken);
        var command = string.Join(' ', arguments.Select(SanitizeArgument));
        var detail = result.Succeeded
            ? "success"
            : result.TimedOut
                ? "timeout"
                : result.WasCancelled
                    ? "cancelled"
                    : $"exit {result.ExitCode}: {Bound(result.StandardError)}";
        await log.WriteAsync(
            $"git {command} -> {detail} ({result.Duration.TotalMilliseconds:N0} ms)",
            CancellationToken.None);
        return result;
    }

    private async Task<GitSynchronizationResult> FailureAsync(
        PluginHostContext context,
        string operation,
        GitCommandResult result,
        CancellationToken cancellationToken)
    {
        var detail = result.TimedOut
            ? "command timed out"
            : result.WasCancelled
                ? "command was cancelled"
                : Bound(result.StandardError);
        var message = $"{operation}: {detail}";
        await LogAndReportAsync(context, message, CancellationToken.None);
        return new GitSynchronizationResult(false, false, message);
    }

    private async Task LogAndReportAsync(
        PluginHostContext context,
        string message,
        CancellationToken cancellationToken)
    {
        await log.WriteAsync(message, cancellationToken);
        context.ReportStatus(message);
    }

    private static IReadOnlyList<string> SplitNullDelimited(string value)
        => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static bool ContainsDotPrefixedSegment(string path)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.StartsWith(".", StringComparison.Ordinal));
    }

    private static string SanitizeArgument(string argument)
        => argument.Contains(' ') ? "<argument>" : argument;

    private static string Bound(string value)
    {
        var normalized = value.Trim();
        return normalized.Length switch
        {
            0 => "no diagnostic output",
            > 1000 => normalized[..1000] + "…",
            _ => normalized
        };
    }
}
