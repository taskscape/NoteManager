using NoteManager.Plugins;

namespace NoteManager.Plugin.GitIntegration;

public sealed class GitIntegrationPlugin : INoteManagerPlugin
{
    internal const string PluginId = "git-integration";

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _scheduler;

    public PluginMetadata Metadata { get; } = new(
        PluginId,
        "Git Integration",
        "Automatically pulls, commits, and pushes an already configured notes repository.",
        "1.0.0");

    public async Task StartAsync(
        PluginHostContext context,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
            var options = GitSynchronizationOptions.LoadOrCreate(
                context.ConfigurationDirectory);
            if (!options.Enabled)
            {
                GitSynchronizationIndicators.ReportError(context);
                context.ReportStatus("Git Integration is activated but disabled in its settings file.");
                return;
            }

            options.Validate();
            _cancellation = new CancellationTokenSource();
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellation.Token);
            _scheduler = RunSchedulerAsync(context, options, linkedCancellation);
            GitSynchronizationIndicators.ReportSynced(context);
            context.ReportStatus(
                $"Git synchronization scheduled every {options.IntervalMinutes:N0} minute(s).");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GitSynchronizationIndicators.ReportError(context);
            throw;
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

    private async Task RunSchedulerAsync(
        PluginHostContext context,
        GitSynchronizationOptions options,
        CancellationTokenSource linkedCancellation)
    {
        using (linkedCancellation)
        {
            var cancellationToken = linkedCancellation.Token;
            var runner = new GitProcessRunner(
                options.GitExecutablePath,
                TimeSpan.FromSeconds(options.CommandTimeoutSeconds));
            var log = new GitSynchronizationLog(context.ConfigurationDirectory);
            var service = new GitSynchronizationService(
                runner,
                log,
                options.CommitMessagePrefix);

            try
            {
                while (true)
                {
                    await Task.Delay(
                        TimeSpan.FromMinutes(options.IntervalMinutes),
                        cancellationToken);
                    await service.SynchronizeAsync(context, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Deactivation, a folder switch, or shutdown ends this scheduler.
            }
            catch (Exception exception)
            {
                GitSynchronizationIndicators.ReportError(context);
                try
                {
                    await log.WriteAsync(
                        $"Git synchronization scheduler stopped: {exception.Message}",
                        CancellationToken.None);
                }
                catch
                {
                    // The UI status remains the final fallback when logging fails.
                }

                context.ReportStatus(
                    $"Git synchronization stopped: {exception.Message}");
            }
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
}
