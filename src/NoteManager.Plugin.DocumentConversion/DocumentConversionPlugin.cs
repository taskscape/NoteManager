using NoteManager.Plugins;

namespace NoteManager.Plugin.DocumentConversion;

public sealed class DocumentConversionPlugin : INoteManagerPlugin
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _scheduler;

    public PluginMetadata Metadata { get; } = new(
        "document-conversion",
        "Document Conversion",
        "Creates missing Markdown counterparts for PDFs, Office files, and other supported documents every five minutes.",
        "1.0.0");

    public async Task StartAsync(
        PluginHostContext context,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
            var options = DocumentConversionOptions.LoadOrCreate(
                context.ConfigurationDirectory);
            if (!options.Enabled)
            {
                context.ReportStatus(
                    "Document Conversion is activated but disabled in its settings file.");
                return;
            }

            options.Validate();
            var executablePath = ResolveCliExecutablePath(options);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "The packaged DOC2MD.Cli executable was not found.",
                    executablePath);
            }

            var log = new DocumentConversionLog(context.ConfigurationDirectory);
            var runner = new Doc2MdProcessRunner(executablePath, options);
            var service = new DocumentConversionService(runner, log);
            _cancellation = new CancellationTokenSource();
            _scheduler = RunSchedulerAsync(
                context,
                options,
                service,
                log,
                _cancellation.Token);
            context.ReportStatus(
                $"Document conversion scheduled every {options.IntervalMinutes:N0} minute(s). First scan is starting now.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _lifecycleLock.Dispose();
    }

    private static async Task RunSchedulerAsync(
        PluginHostContext context,
        DocumentConversionOptions options,
        DocumentConversionService service,
        DocumentConversionLog log,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunCycleAsync(context, service, log, cancellationToken);
            using var timer = new PeriodicTimer(
                TimeSpan.FromMinutes(options.IntervalMinutes));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunCycleAsync(context, service, log, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Deactivation, a folder switch, or shutdown ends this scheduler.
        }
    }

    private static async Task RunCycleAsync(
        PluginHostContext context,
        DocumentConversionService service,
        DocumentConversionLog log,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.ConvertPendingAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Document conversion scan failed: {exception.Message}";
            try
            {
                await log.WriteAsync(message, CancellationToken.None);
            }
            catch
            {
                // The UI status remains the final fallback when logging fails.
            }

            context.ReportStatus(message);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var cancellation = _cancellation;
        var scheduler = _scheduler;
        _cancellation = null;
        _scheduler = null;
        cancellation?.Cancel();

        if (scheduler is not null)
        {
            try
            {
                await scheduler.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellation?.IsCancellationRequested == true
                || cancellationToken.IsCancellationRequested)
            {
                // Expected when plugin activity is being stopped.
            }
        }

        cancellation?.Dispose();
    }

    private static string ResolveCliExecutablePath(
        DocumentConversionOptions options)
    {
        var pluginDirectory = Path.GetDirectoryName(
                                  typeof(DocumentConversionPlugin).Assembly.Location)
                              ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(options.CliExecutablePath))
        {
            return Path.Combine(
                pluginDirectory,
                "Assets",
                "DOC2MD.Cli",
                "DOC2MD.Cli.exe");
        }

        return Path.GetFullPath(
            options.CliExecutablePath,
            pluginDirectory);
    }
}
