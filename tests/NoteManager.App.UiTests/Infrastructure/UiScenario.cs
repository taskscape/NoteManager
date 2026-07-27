using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace NoteManager.App.UiTests.Infrastructure;

internal static class UiScenario
{
    public static void WaitForNoteCount(
        NoteManagerAppSession session,
        int count,
        TimeSpan? timeout = null)
        => session.WaitForTextByAutomationId(
            "VisibleNoteCountText",
            $"{count:N0} notes",
            timeout);

    public static void SelectNoteBySearch(
        NoteManagerAppSession session,
        string fileName)
    {
        session.SetText("SearchBox", fileName);
        WaitForNoteCount(session, 1);
        session.SelectListItem("NotesList", fileName);
        session.WaitForTextByAutomationId(
            "SelectedNoteTitle",
            fileName);
    }

    public static void WaitForFileContent(
        string path,
        Func<string, bool> predicate,
        string description)
        => UiWait.Until(
            () => File.Exists(path)
                  && predicate(File.ReadAllText(path)),
            description);

    public static CheckBox WaitForCheckBox(
        AutomationElement root,
        string name)
        => UiWait.UntilNotNull(
            () => root.FindFirstDescendant(conditionFactory =>
                    conditionFactory
                        .ByControlType(ControlType.CheckBox)
                        .And(conditionFactory.ByName(name)))
                ?.AsCheckBox(),
            $"checkbox '{name}'");

    public static void InvokeNativeDialogButton(
        Window dialog,
        string automationId,
        params string[] fallbackNames)
    {
        var button = UiWait.UntilNotNull(
            () =>
            {
                var byId = dialog.FindFirstDescendant(automationId);
                if (byId is not null)
                {
                    return byId.AsButton();
                }

                foreach (var name in fallbackNames)
                {
                    var byName = dialog.FindFirstDescendant(conditionFactory =>
                        conditionFactory
                            .ByControlType(ControlType.Button)
                            .And(conditionFactory.ByName(name)));
                    if (byName is not null)
                    {
                        return byName.AsButton();
                    }
                }

                return null;
            },
            $"dialog button '{automationId}'");
        button.Invoke();
    }

    public static int CountTagHeaders(string markdown)
    {
        var count = 0;
        using var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            if (line.TrimStart().StartsWith(
                    "tags:",
                    StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
