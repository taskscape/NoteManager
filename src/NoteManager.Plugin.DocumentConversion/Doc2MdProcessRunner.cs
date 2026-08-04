using System.Diagnostics;

namespace NoteManager.Plugin.DocumentConversion;

public sealed record Doc2MdProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool WasCancelled,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !WasCancelled && !TimedOut;
}

public interface IDoc2MdProcessRunner
{
    Task<Doc2MdProcessResult> ConvertFileAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default);
}

public sealed class Doc2MdProcessRunner(
    string executablePath,
    DocumentConversionOptions options) : IDoc2MdProcessRunner
{
    public async Task<Doc2MdProcessResult> ConvertFileAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            return new Doc2MdProcessResult(
                -1,
                string.Empty,
                $"DOC2MD was not found at '{executablePath}'. Reinstall DOC2MD in its default location and restart NoteManager.",
                TimeSpan.Zero,
                false,
                false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
                               ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildArguments(inputPath, outputPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("DOC2MD.Cli did not start.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            stopwatch.Stop();
            return new Doc2MdProcessResult(
                -1,
                string.Empty,
                exception.Message,
                stopwatch.Elapsed,
                false,
                false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCancellation = new CancellationTokenSource(
            TimeSpan.FromMinutes(options.CommandTimeoutMinutes));
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

        var standardOutput = await stdoutTask;
        var standardError = await stderrTask;
        stopwatch.Stop();
        return new Doc2MdProcessResult(
            process.HasExited ? process.ExitCode : -1,
            standardOutput,
            standardError,
            stopwatch.Elapsed,
            wasCancelled,
            timedOut);
    }

    internal IReadOnlyList<string> BuildArguments(
        string inputPath,
        string outputPath)
    {
        var arguments = new List<string>
        {
            "convert",
            "--input",
            inputPath,
            "--output",
            outputPath,
            "--json"
        };
        arguments.Add("--pdf-processing");
        arguments.Add(options.PdfProcessing);
        if (options.PdfProcessing.Equals("local", StringComparison.Ordinal))
        {
            arguments.Add("--ocr-languages");
            arguments.Add(options.OcrLanguages);
        }

        return arguments;
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
