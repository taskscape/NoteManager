using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NoteManager.Desktop.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
        : this("Confirm", "Continue?")
    {
    }

    public ConfirmDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}
