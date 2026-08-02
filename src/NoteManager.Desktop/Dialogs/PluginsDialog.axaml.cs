using Avalonia.Controls;
using Avalonia.Interactivity;
using NoteManager.Desktop.Services;

namespace NoteManager.Desktop.Dialogs;

public partial class PluginsDialog : Window
{
    private readonly PluginManager? _pluginManager;
    private bool _changingActivation;

    public PluginsDialog()
    {
        InitializeComponent();
        VaultPathText.Text = "Open a notes folder to activate plugins.";
    }

    public PluginsDialog(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        InitializeComponent();
        PluginItems.ItemsSource = pluginManager.Plugins;
        VaultPathText.Text = pluginManager.VaultPath is null
            ? "Open a notes folder to activate plugins."
            : $"Configuration: {Path.Combine(pluginManager.VaultPath, ".note", "plugins")}";
    }

    private async void PluginActivation_OnClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (_changingActivation
            || _pluginManager is null
            || sender is not CheckBox checkBox
            || checkBox.DataContext is not PluginListItemViewModel plugin)
        {
            return;
        }

        _changingActivation = true;
        try
        {
            await _pluginManager.SetEnabledAsync(
                plugin,
                checkBox.IsChecked == true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            plugin.Status = exception.Message;
        }
        finally
        {
            checkBox.IsChecked = plugin.IsEnabled;
            _changingActivation = false;
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
