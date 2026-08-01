using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace NoteManager.Desktop.UiTests.Infrastructure;

internal sealed class NoteManagerAppSession : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Application _application;
    private bool _disposed;

    private NoteManagerAppSession(
        Application application,
        UIA3Automation automation,
        Window mainWindow)
    {
        _application = application;
        _automation = automation;
        MainWindow = mainWindow;
    }

    public Window MainWindow { get; }

    public static NoteManagerAppSession Launch(string folderPath)
    {
        var startInfo = new ProcessStartInfo(UiTestPaths.ApplicationExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(
                UiTestPaths.ApplicationExecutable)
        };
        startInfo.ArgumentList.Add("--folder");
        startInfo.ArgumentList.Add(folderPath);

        var application = Application.Launch(startInfo);
        var automation = new UIA3Automation();
        try
        {
            application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(15));
            var mainWindow = application.GetMainWindow(
                automation,
                TimeSpan.FromSeconds(15))
                ?? throw new InvalidOperationException(
                    "NoteManager did not expose a main window.");
            var session = new NoteManagerAppSession(
                application,
                automation,
                mainWindow);
            session.WaitForByAutomationId("SearchBox");
            return session;
        }
        catch
        {
            automation.Dispose();
            if (!application.HasExited)
            {
                application.Kill();
            }

            application.Dispose();
            throw;
        }
    }

    public AutomationElement WaitForByAutomationId(
        string automationId,
        TimeSpan? timeout = null)
        => UiWait.UntilNotNull(
            () => MainWindow.FindFirstDescendant(conditionFactory =>
                conditionFactory.ByAutomationId(automationId)),
            $"automation id '{automationId}'",
            timeout);

    public void SetSearchText(string value)
    {
        var searchBox = WaitForByAutomationId("SearchBox").AsTextBox();
        if (searchBox.IsReadOnly)
        {
            throw new InvalidOperationException("The search box is read-only.");
        }

        searchBox.Text = value;
    }

    public void TypeSearchAndPressEnter(string value)
    {
        MainWindow.Focus();
        var searchBox = WaitForByAutomationId("SearchBox").AsTextBox();
        searchBox.Focus();
        Keyboard.Type(value);
        Keyboard.Press(VirtualKeyShort.RETURN);
    }

    public void WaitForNoteCount(int expectedCount)
        => WaitForElementText(
            "VisibleNoteCountText",
            $"{expectedCount:N0} notes");

    public void WaitForDisplayedNoteItems(int expectedCount)
        => UiWait.Until(
            () => WaitForByAutomationId("NotesList")
                .AsListBox()
                .Items.Length == expectedCount,
            $"notes list to contain {expectedCount:N0} items");

    public void WaitForNoSearchResults()
        => WaitForElementText("NoSearchResultsText", "No notes found");

    public void WaitForSelectedTitle(string expectedTitle)
        => WaitForElementText("SelectedNoteTitle", expectedTitle);

    public void RenameSelectedNoteAndPressEnter(string fileName)
    {
        WaitForByAutomationId("SelectedNoteTitle").AsButton().Invoke();
        var editor = WaitForByAutomationId("SelectedNoteTitleEditor").AsTextBox();
        editor.Text = fileName;
        editor.Focus();
        Keyboard.Press(VirtualKeyShort.RETURN);
    }

    public void RenameSelectedNoteAndLeaveEditor(string fileName)
    {
        WaitForByAutomationId("SelectedNoteTitle").AsButton().Invoke();
        var editor = WaitForByAutomationId("SelectedNoteTitleEditor").AsTextBox();
        editor.Text = fileName;
        WaitForByAutomationId("SearchBox").AsTextBox().Focus();
    }

    public void WaitForIndexReady()
    {
        WaitForElementText("SearchIndexStatusText", "Full-text ready");
        WaitForSearchBoxEnabled(expected: true);
    }

    public void WaitForSearchBoxEnabled(bool expected)
        => UiWait.Until(
            () => WaitForByAutomationId("SearchBox").IsEnabled == expected,
            $"search box enabled state to be {expected}");

    public void WaitForSearchBoxName(string expected)
        => UiWait.Until(
            () => WaitForByAutomationId("SearchBox").Name.Equals(
                expected,
                StringComparison.Ordinal),
            $"search box name to be '{expected}'");

    public void WaitForStatusContaining(string expectedText)
        => UiWait.Until(
            () => GetElementText(WaitForByAutomationId("StatusText"))
                .Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            $"status text containing '{expectedText}'");

    public void WaitForSortButtonEnabled(bool expected)
        => UiWait.Until(
            () => WaitForByAutomationId("SortNotesButton").IsEnabled == expected,
            $"sort button enabled state to be {expected}");

    public void SelectNavigationItem(string itemName)
    {
        var list = WaitForByAutomationId("NavigationList").AsListBox();
        var item = UiWait.UntilNotNull(
            () => FindContainingListBoxItem(
                list.FindFirstDescendant(conditionFactory =>
                    conditionFactory.ByName(itemName))),
            $"navigation item '{itemName}'");
        item.ScrollIntoView();
        item.Select();
        UiWait.Until(
            () => item.IsSelected,
            $"navigation item '{itemName}' to be selected");
    }

    public string DumpAutomationTree()
    {
        var builder = new System.Text.StringBuilder();
        var remaining = 500;
        WriteElement(MainWindow, builder, depth: 0, maxDepth: 7, ref remaining);
        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_application.HasExited
                && !_application.Close(killIfCloseFails: false))
            {
                _application.Kill();
            }
        }
        finally
        {
            _automation.Dispose();
            _application.Dispose();
        }
    }

    private void WaitForElementText(string automationId, string expectedText)
        => UiWait.Until(
            () => GetElementText(WaitForByAutomationId(automationId))
                .Equals(expectedText, StringComparison.Ordinal),
            $"'{automationId}' to display '{expectedText}'");

    private static string GetElementText(AutomationElement element)
        => element.ControlType == ControlType.Edit
            ? element.AsTextBox().Text
            : element.Name;

    private static void WriteElement(
        AutomationElement element,
        System.Text.StringBuilder builder,
        int depth,
        int maxDepth,
        ref int remaining)
    {
        if (remaining-- <= 0)
        {
            return;
        }

        builder
            .Append(' ', depth * 2)
            .Append(ReadAutomationProperty(
                () => element.ControlType.ToString(),
                "<unsupported>"))
            .Append(" Id='")
            .Append(ReadAutomationProperty(() => element.AutomationId))
            .Append("' Name='")
            .Append(ReadAutomationProperty(() => element.Name))
            .AppendLine("'");
        if (depth >= maxDepth)
        {
            return;
        }

        foreach (var child in element.FindAllChildren())
        {
            WriteElement(
                child,
                builder,
                depth + 1,
                maxDepth,
                ref remaining);
        }
    }

    private static ListBoxItem? FindContainingListBoxItem(
        AutomationElement? element)
    {
        while (element is not null
               && element.ControlType != ControlType.ListItem)
        {
            element = element.Parent;
        }

        return element?.AsListBoxItem();
    }

    private static string ReadAutomationProperty(
        Func<string> read,
        string unsupportedValue = "")
    {
        try
        {
            return read();
        }
        catch (NotSupportedException)
        {
            return unsupportedValue;
        }
    }
}
