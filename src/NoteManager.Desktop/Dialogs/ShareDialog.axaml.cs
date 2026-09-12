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
        try
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
                // Pass the exception so the application log retains its complete clipboard failure details.
                ViewModel.ReportClipboardFailure(exception.Message, exception);
            }
        }
        catch (Exception exception)
        {
            // Async event handlers cannot return faults to their caller, so contain unexpected upload failures here.
            ViewModel.ReportUnexpectedPublishingFailure(exception);
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsPublishing)
        {
            Close();
        }
    }

    private void CancelPublishing_OnClick(object? sender, RoutedEventArgs e)
    {
        // Cancel before dismissing so a stalled HTTP body cannot keep this modal visible.
        ViewModel.CancelPublishing();
        Close();
    }

    private void ShareDialog_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (ViewModel.IsPublishing)
        {
            // Window controls, Alt+F4, and Escape must share the explicit Cancel action's safe exit behavior.
            ViewModel.CancelPublishing();
        }
    }

    private void ShareDialog_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Escape closes the dialog during publishing; its Closing handler cancels the active operation first.
            Close();
        }
    }
}
