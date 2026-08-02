namespace NoteManager.Plugins;

public interface INoteManagerPlugin : IAsyncDisposable
{
    PluginMetadata Metadata { get; }

    Task StartAsync(
        PluginHostContext context,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record PluginMetadata(
    string Id,
    string Name,
    string Description,
    string Version);

public sealed record PluginHostContext(
    string VaultPath,
    string ConfigurationDirectory,
    Func<CancellationToken, Task<bool>> SaveActiveNoteAsync,
    Action<string> ReportStatus);
