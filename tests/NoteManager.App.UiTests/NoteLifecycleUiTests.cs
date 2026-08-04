using NUnit.Framework;
using NoteManager.App.UiTests.Infrastructure;

namespace NoteManager.App.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("EndToEnd")]
internal sealed class NoteLifecycleUiTests : UiTestBase
{
    [Test]
    public void EditsSaveOnNoteSwitch_ViewCommand_AndApplicationClose()
    {
        Vault.CreateStandardDataset();
        var app = Launch();
        UiScenario.WaitForNoteCount(app, 7);

        const string firstEdit =
            "# Current saved on note switch\nwith another line.";
        app.SetText("MarkdownEditor", firstEdit);
        UiScenario.SelectNoteBySearch(app, "06 Second editable.md");
        UiScenario.WaitForFileContent(
            Vault.CurrentNotePath,
            content => content == firstEdit,
            "the first note to save on note selection");

        const string secondEdit = "# Second saved on view switch";
        app.SetText("MarkdownEditor", secondEdit);
        app.Invoke("CardViewButton");
        UiScenario.WaitForFileContent(
            Vault.SecondEditableNotePath,
            content => content == secondEdit,
            "the second note to save on view change");

        const string closingEdit = "# Second saved while closing";
        app.SetText("MarkdownEditor", closingEdit);
        app.CloseGracefully();
        app.WaitForExit();
        UiScenario.WaitForFileContent(
            Vault.SecondEditableNotePath,
            content => content == closingEdit,
            "the selected note to save during shutdown");

        Assert.That(
            Directory.EnumerateFiles(
                Vault.RootPath,
                "*.tmp",
                SearchOption.AllDirectories),
            Is.Empty,
            "Atomic-save temporary files were left behind.");
    }

    [Test]
    public void CreateAndDelete_RespectCancellationAndConfirmation()
    {
        Vault.CreateStandardDataset();
        var app = Launch();
        UiScenario.WaitForNoteCount(app, 7);

        app.Invoke("CreateToolbarButton");
        var createdPath = Path.Combine(Vault.RootPath, "Untitled note.md");
        UiWait.Until(
            () => File.Exists(createdPath),
            "the new root Markdown file");
        UiScenario.WaitForNoteCount(app, 8);
        app.WaitForTextByAutomationId(
            "SelectedNoteTitle",
            "Untitled note.md");
        Assert.That(new FileInfo(createdPath).Length, Is.Zero);

        app.Invoke("DeleteToolbarButton");
        var confirmation = app.WaitForWindow("Delete note");
        UiScenario.InvokeNativeDialogButton(
            confirmation,
            "7",
            "No",
            "Nie");
        app.WaitForWindowToClose("Delete note");
        Assert.That(File.Exists(createdPath), Is.True);

        app.Invoke("DeleteToolbarButton");
        confirmation = app.WaitForWindow("Delete note");
        UiScenario.InvokeNativeDialogButton(
            confirmation,
            "6",
            "Yes",
            "Tak");
        app.WaitForWindowToClose("Delete note");
        UiWait.Until(
            () => !File.Exists(createdPath),
            "the confirmed note deletion");
        UiScenario.WaitForNoteCount(app, 7);
    }
}
