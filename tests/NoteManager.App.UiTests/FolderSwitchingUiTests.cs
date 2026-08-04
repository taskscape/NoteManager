using NUnit.Framework;
using NoteManager.App.UiTests.Infrastructure;

namespace NoteManager.App.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("EndToEnd")]
internal sealed class FolderSwitchingUiTests : UiTestBase
{
    [Test]
    public async Task InjectedFolderSwitch_CancelsOldIndex_WithoutOpeningDialog()
    {
        Vault.CreateStandardDataset();
        Vault.AddTagCatalog(120);
        Vault.CreateAlternateDataset();
        var pipeName =
            $"NoteManager.UiTests.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var app = Launch(automationPipeName: pipeName);

        UiScenario.WaitForNoteCount(app, 127);
        await app.SwitchFolderAsync(pipeName, Vault.AlternateRootPath);
        UiScenario.WaitForNoteCount(app, 1);
        app.WaitForTextByAutomationId(
            "SelectedNoteTitle",
            "Switched folder note.md");
        app.WaitForTextByAutomationId(
            "SearchIndexStatusText",
            "Full-text ready");

        app.SetText("SearchBox", "changed-folder-body-token");
        UiScenario.WaitForNoteCount(app, 1);
        Assert.That(
            app.WaitForByAutomationId("MarkdownEditor").AsTextBox().Text,
            Does.Contain("changed-folder-body-token"));
        Assert.That(
            app.MainWindow.ModalWindows,
            Is.Empty,
            "The test-only folder switch must not open a native picker.");
    }
}
