using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace NoteManager.App.UiTests.Infrastructure;

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
    public int ProcessId => _application.ProcessId;

    public static NoteManagerAppSession Launch(
        string folderPath,
        Uri? infostackerBaseUri = null,
        string? automationPipeName = null)
    {
        var startInfo = new ProcessStartInfo(UiTestPaths.ApplicationExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(
                UiTestPaths.ApplicationExecutable)
        };
        startInfo.ArgumentList.Add("--folder");
        startInfo.ArgumentList.Add(folderPath);
        if (infostackerBaseUri is not null)
        {
            startInfo.ArgumentList.Add("--infostacker-base-url");
            startInfo.ArgumentList.Add(infostackerBaseUri.AbsoluteUri);
        }

        if (!string.IsNullOrWhiteSpace(automationPipeName))
        {
            startInfo.ArgumentList.Add("--automation-pipe");
            startInfo.ArgumentList.Add(automationPipeName);
        }

        var application = Application.Launch(startInfo);
        var automation = new UIA3Automation();
        try
        {
            application.WaitWhileMainHandleIsMissing(
                TimeSpan.FromSeconds(15));
            var mainWindow = application.GetMainWindow(
                automation,
                TimeSpan.FromSeconds(15))
                ?? throw new InvalidOperationException(
                    "NoteManager did not expose a main window.");
            var session = new NoteManagerAppSession(
                application,
                automation,
                mainWindow);
            session.WaitForByAutomationId("VisibleNoteCountText");
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
        AutomationElement? root = null,
        TimeSpan? timeout = null)
        => UiWait.UntilNotNull(
            () =>
            {
                if (root is not null)
                {
                    return root.FindFirstDescendant(automationId);
                }

                return MainWindow.FindFirstDescendant(automationId)
                       ?? _automation
                           .GetDesktop()
                           .FindFirstDescendant(conditionFactory =>
                               conditionFactory.ByAutomationId(
                                   automationId));
            },
            $"automation id '{automationId}'",
            timeout);

    public AutomationElement WaitForByName(
        string name,
        AutomationElement? root = null,
        ControlType? controlType = null,
        TimeSpan? timeout = null)
        => UiWait.UntilNotNull(
            () => (root ?? MainWindow).FindFirstDescendant(conditionFactory =>
            {
                var condition = conditionFactory.ByName(name);
                return controlType is null
                    ? condition
                    : condition.And(
                        conditionFactory.ByControlType(controlType.Value));
            }),
            $"UI element named '{name}'",
            timeout);

    public Window WaitForWindow(
        string title,
        TimeSpan? timeout = null)
        => UiWait.UntilNotNull(
            () => FindTopLevelWindow(title),
            $"window '{title}'",
            timeout);

    public void WaitForWindowToClose(
        string title,
        TimeSpan? timeout = null)
        => UiWait.Until(
            () => FindTopLevelWindow(title) is null,
            $"window '{title}' to close",
            timeout);

    public void WaitForTextByAutomationId(
        string automationId,
        string expectedText,
        TimeSpan? timeout = null)
        => UiWait.Until(
            () =>
            {
                var element = WaitForByAutomationId(
                    automationId,
                    timeout: TimeSpan.FromSeconds(2));
                var displayedText = element.ControlType == ControlType.Edit
                    ? element.AsTextBox().Text
                    : element.Name;
                return displayedText.Equals(
                    expectedText,
                    StringComparison.Ordinal);
            },
            $"'{automationId}' to display '{expectedText}'",
            timeout);

    public void WaitForText(string expectedText, TimeSpan? timeout = null)
        => WaitForByName(
            expectedText,
            controlType: ControlType.Text,
            timeout: timeout);

    public void Invoke(string automationId, AutomationElement? root = null)
        => WaitForByAutomationId(automationId, root)
            .AsButton()
            .Invoke();

    public void SetText(
        string automationId,
        string value,
        AutomationElement? root = null)
    {
        var textBox = WaitForByAutomationId(automationId, root).AsTextBox();
        if (textBox.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"The text box '{automationId}' is read-only.");
        }

        textBox.Text = value;
    }

    public void SelectListItem(string listAutomationId, string itemName)
    {
        var list = WaitForByAutomationId(listAutomationId).AsListBox();
        var item = UiWait.UntilNotNull(
            () => list
                .FindFirstDescendant(conditionFactory =>
                    conditionFactory
                        .ByControlType(ControlType.ListItem)
                        .And(conditionFactory.ByName(itemName)))
                ?.AsListBoxItem(),
            $"list item '{itemName}' in '{listAutomationId}'");
        item.ScrollIntoView();
        item.Select();
        UiWait.Until(
            () => list.SelectedItem?.Name.Equals(
                itemName,
                StringComparison.Ordinal) == true,
            $"'{itemName}' to become selected in '{listAutomationId}'");
    }

    public AutomationElement[] FindAllByAutomationId(
        string automationId,
        AutomationElement? root = null)
        => (root ?? MainWindow)
            .FindAllDescendants(conditionFactory =>
                conditionFactory.ByAutomationId(automationId));

    public void CloseGracefully()
    {
        if (!_application.HasExited)
        {
            _application.Close(killIfCloseFails: false);
        }
    }

    public void WaitForExit(TimeSpan? timeout = null)
        => UiWait.Until(
            () => _application.HasExited,
            "NoteManager process to exit",
            timeout ?? TimeSpan.FromSeconds(10));

    public async Task SwitchFolderAsync(
        string pipeName,
        string folderPath,
        CancellationToken cancellationToken = default)
        => await SendAutomationCommandAsync(
            pipeName,
            $"folder|{folderPath}",
            cancellationToken);

    public async Task ImportPdfAsync(
        string pipeName,
        string pdfPath,
        CancellationToken cancellationToken = default)
        => await SendAutomationCommandAsync(
            pipeName,
            $"import-pdf|{pdfPath}",
            cancellationToken);

    public async Task OpenSharePanelAsync(
        string pipeName,
        CancellationToken cancellationToken = default)
        => await SendAutomationCommandAsync(
            pipeName,
            "open-share",
            cancellationToken);

    private static async Task SendAutomationCommandAsync(
        string pipeName,
        string command,
        CancellationToken cancellationToken)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000, cancellationToken);
        await using var writer = new StreamWriter(
            client,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(command.AsMemory(), cancellationToken);
        using var reader = new StreamReader(client, leaveOpen: true);
        var response = await reader.ReadLineAsync(cancellationToken);
        if (!string.Equals(response, "OK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The NoteManager automation command failed: {response ?? "no response"}");
        }
    }

    public void SaveScreenshot(string path)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Screenshot directory is missing."));
        MainWindow.CaptureToFile(path);
    }

    public string DumpAutomationTree()
    {
        var builder = new System.Text.StringBuilder();
        var remaining = 600;
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

    private static void WriteElement(
        AutomationElement element,
        System.Text.StringBuilder builder,
        int depth,
        int maxDepth,
        ref int remaining)
    {
        try
        {
            if (remaining-- <= 0)
            {
                return;
            }

            builder
                .Append(' ', depth * 2)
                .Append(ReadAutomationProperty(
                    () => element.ControlType.ToString()))
                .Append(" id='")
                .Append(ReadAutomationProperty(() => element.AutomationId))
                .Append("' name='")
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
                if (remaining <= 0)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            builder
                .Append(' ', (depth + 1) * 2)
                .Append("<tree unavailable: ")
                .Append(exception.Message)
                .AppendLine(">");
        }
    }

    private static string ReadAutomationProperty(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return "<unavailable>";
        }
    }

    private Window? FindTopLevelWindow(string title)
    {
        var applicationWindow = _application
            .GetAllTopLevelWindows(_automation)
            .FirstOrDefault(window => window.Name.Equals(
                title,
                StringComparison.Ordinal));
        if (applicationWindow is not null)
        {
            return applicationWindow;
        }

        var desktopWindow = _automation
            .GetDesktop()
            .FindFirstChild(conditionFactory =>
                conditionFactory
                    .ByControlType(ControlType.Window)
                    .And(conditionFactory.ByName(title)))
            ?.AsWindow();
        if (desktopWindow is not null)
        {
            return desktopWindow;
        }

        // Native WPF MessageBox windows are not consistently exposed as
        // top-level UIA children on every Windows version. Resolve the owned
        // HWND as a final fallback, then return it through the same UIA3
        // automation session.
        var handle = FindNativeWindowHandle(ProcessId, title);
        return handle == IntPtr.Zero
            ? null
            : _automation.FromHandle(handle).AsWindow();
    }

    private static IntPtr FindNativeWindowHandle(
        int processId,
        string expectedTitle)
    {
        var matchingHandle = IntPtr.Zero;
        EnumWindows(
            (handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var ownerProcessId);
                if (ownerProcessId != processId)
                {
                    return true;
                }

                var titleLength = GetWindowTextLength(handle);
                if (titleLength == 0)
                {
                    return true;
                }

                var title = new StringBuilder(titleLength + 1);
                _ = GetWindowText(handle, title, title.Capacity);
                if (!title.ToString().Equals(
                        expectedTitle,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                matchingHandle = handle;
                return false;
            },
            IntPtr.Zero);
        return matchingHandle;
    }

    private delegate bool EnumWindowsCallback(
        IntPtr windowHandle,
        IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder text,
        int maximumLength);
}
