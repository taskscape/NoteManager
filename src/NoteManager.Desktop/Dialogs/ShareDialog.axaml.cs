using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using NoteManager.App.ViewModels;

namespace NoteManager.Desktop.Dialogs;

public partial class ShareDialog : Window
{
    public ShareDialog()
    {
        InitializeComponent();
    }

    public ShareDialog(MainViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Opened += (_, _) => PublishPublicLinkButton.Focus();
        Closing += ShareDialog_OnClosing;
        KeyDown += ShareDialog_OnKeyDown;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private async void Publish_OnClick(object? sender, RoutedEventArgs e)
    {
        var publicUrl = await ViewModel.PublishSelectedNoteAsync();
        if (publicUrl is null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ViewModel.ReportClipboardFailure("The system clipboard is unavailable.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(publicUrl);
            ViewModel.ConfirmPublicLinkCopied(publicUrl);
        }
        catch (Exception exception)
        {
            ViewModel.ReportClipboardFailure(exception.Message);
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsPublishing)
        {
            Close();
        }
    }

    private void ShareDialog_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (ViewModel.IsPublishing)
        {
            e.Cancel = true;
        }
    }

    private void ShareDialog_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !ViewModel.IsPublishing)
        {
            e.Handled = true;
            Close();
        }
    }
}
