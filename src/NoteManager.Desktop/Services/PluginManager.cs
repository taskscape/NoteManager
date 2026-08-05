using System.Collections.ObjectModel;
using NoteManager.App.Infrastructure;
using NoteManager.Plugins;

namespace NoteManager.Desktop.Services;

public sealed class PluginListItemViewModel : ObservableObject
{
    private bool _isEnabled;
    private bool _isRunning;
    private string _status;

    internal PluginListItemViewModel(DiscoveredPlugin discoveredPlugin)
    {
        DiscoveredPlugin = discoveredPlugin;
        Name = discoveredPlugin.Instance?.Metadata.Name
               ?? Path.GetFileNameWithoutExtension(discoveredPlugin.AssemblyPath);
        Description = discoveredPlugin.Instance?.Metadata.Description
                      ?? "This plugin could not be loaded.";
        Version = discoveredPlugin.Instance?.Metadata.Version ?? string.Empty;
        Id = discoveredPlugin.Instance?.Metadata.Id
             ?? Path.GetFileNameWithoutExtension(discoveredPlugin.AssemblyPath);
        _status = discoveredPlugin.LoadError is null
            ? "Available"
            : $"Load failed: {discoveredPlugin.LoadError}";
    }

    internal DiscoveredPlugin DiscoveredPlugin { get; }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public string Version { get; }

    public bool IsAvailable => DiscoveredPlugin.IsAvailable;

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        internal set => SetProperty(ref _isRunning, value);
    }

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public bool CanChangeActivation { get; internal set; }

    internal void NotifyActivationAvailabilityChanged()
        => OnPropertyChanged(nameof(CanChangeActivation));
}

public sealed class PluginManager
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly PluginActivationStore _activationStore = new();
    private readonly Func<CancellationToken, Task<bool>> _saveActiveNoteAsync;
    private readonly Action<string> _reportStatus;
    private readonly Action<PluginIndicatorStatus> _reportIndicatorStatus;
    private readonly Action<string, bool> _reportIndicatorVisibility;
    private HashSet<string> _enabledPluginIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _vaultPath;

    public PluginManager(
        string applicationDirectory,
        Func<CancellationToken, Task<bool>> saveActiveNoteAsync,
        Action<string> reportStatus,
        Action<PluginIndicatorStatus> reportIndicatorStatus,
        Action<string, bool> reportIndicatorVisibility)
    {
        _saveActiveNoteAsync = saveActiveNoteAsync;
        _reportStatus = reportStatus;
        _reportIndicatorStatus = reportIndicatorStatus;
        _reportIndicatorVisibility = reportIndicatorVisibility;

        var catalog = new PluginCatalog();
        Plugins = new ObservableCollection<PluginListItemViewModel>(
            catalog.Discover(applicationDirectory)
                .Select(plugin => new PluginListItemViewModel(plugin))
                .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase));
    }

    public ObservableCollection<PluginListItemViewModel> Plugins { get; }

    public string? VaultPath => _vaultPath;

    public async Task SetVaultAsync(
        string vaultPath,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopAllCoreAsync(cancellationToken);
            _vaultPath = Path.GetFullPath(vaultPath);
            try
            {
                _enabledPluginIds = new HashSet<string>(
                    _activationStore.Load(_vaultPath),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or System.Text.Json.JsonException)
            {
                _enabledPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _reportStatus($"Plugin activation configuration could not be read: {exception.Message}");
            }

            foreach (var entry in Plugins)
            {
                _reportIndicatorVisibility(entry.Id, false);
                entry.CanChangeActivation = entry.IsAvailable;
                entry.NotifyActivationAvailabilityChanged();
                entry.IsEnabled = _enabledPluginIds.Contains(entry.Id);
                entry.Status = entry.IsEnabled ? "Starting…" : "Available";
                if (entry.IsEnabled)
                {
                    await StartEntryAsync(entry, cancellationToken);
                }
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task SetEnabledAsync(
        PluginListItemViewModel entry,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_vaultPath is null)
            {
                throw new InvalidOperationException(
                    "Open a notes folder before changing plugin activation.");
            }

            if (!entry.IsAvailable)
            {
                throw new InvalidOperationException("This plugin could not be loaded.");
            }

            if (enabled)
            {
                entry.Status = "Starting…";
                await StartEntryAsync(entry, cancellationToken);
                if (!entry.IsRunning)
                {
                    entry.IsEnabled = false;
                    throw new InvalidOperationException(entry.Status);
                }

                _enabledPluginIds.Add(entry.Id);
                entry.IsEnabled = true;
            }
            else
            {
                await StopEntryAsync(entry, cancellationToken);
                _reportIndicatorVisibility(entry.Id, false);
                _enabledPluginIds.Remove(entry.Id);
                entry.IsEnabled = false;
                entry.Status = "Available";
            }

            _activationStore.Save(_vaultPath, _enabledPluginIds);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopAllCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartEntryAsync(
        PluginListItemViewModel entry,
        CancellationToken cancellationToken)
    {
        if (_vaultPath is null || entry.DiscoveredPlugin.Instance is null)
        {
            return;
        }

        try
        {
            var configurationDirectory = _activationStore
                .GetPluginConfigurationDirectory(_vaultPath, entry.Id);
            var context = new PluginHostContext(
                _vaultPath,
                configurationDirectory,
                _saveActiveNoteAsync,
                _reportStatus,
                _reportIndicatorStatus);
            await entry.DiscoveredPlugin.Instance.StartAsync(context, cancellationToken);
            entry.IsRunning = true;
            entry.Status = "Active";
            _reportIndicatorVisibility(entry.Id, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            entry.IsRunning = false;
            entry.Status = $"Could not start: {exception.Message}";
            _reportIndicatorVisibility(entry.Id, false);
            _reportStatus($"{entry.Name} could not start: {exception.Message}");
        }
    }

    private static async Task StopEntryAsync(
        PluginListItemViewModel entry,
        CancellationToken cancellationToken)
    {
        if (!entry.IsRunning || entry.DiscoveredPlugin.Instance is null)
        {
            return;
        }

        await entry.DiscoveredPlugin.Instance.StopAsync(cancellationToken);
        entry.IsRunning = false;
    }

    private async Task StopAllCoreAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in Plugins.Where(plugin => plugin.IsRunning))
        {
            await StopEntryAsync(entry, cancellationToken);
            _reportIndicatorVisibility(entry.Id, false);
            entry.Status = entry.IsEnabled ? "Inactive" : "Available";
        }
    }
}
