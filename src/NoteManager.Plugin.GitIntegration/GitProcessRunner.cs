using System.Diagnostics;

namespace NoteManager.Plugin.GitIntegration;

public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool WasCancelled,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !WasCancelled && !TimedOut;
}

public sealed class GitProcessRunner(
    string executablePath,
    TimeSpan timeout)
{
    public async Task<GitCommandResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryPath
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The Git process did not start.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            stopwatch.Stop();
            return new GitCommandResult(
                -1,
                string.Empty,
                exception.Message,
                stopwatch.Elapsed,
                false,
                false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        var wasCancelled = false;
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = cancellationToken.IsCancellationRequested;
            timedOut = !wasCancelled && timeoutCancellation.IsCancellationRequested;
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var stdout = await ReadCompletedOutputAsync(stdoutTask);
        var stderr = await ReadCompletedOutputAsync(stderrTask);
        stopwatch.Stop();
        return new GitCommandResult(
            process.HasExited ? process.ExitCode : -1,
            stdout,
            stderr,
            stopwatch.Elapsed,
            wasCancelled,
            timedOut);
    }

    private static async Task<string> ReadCompletedOutputAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
    }
}
