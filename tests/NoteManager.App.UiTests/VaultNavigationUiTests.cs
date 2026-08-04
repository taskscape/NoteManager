using NUnit.Framework;
using NoteManager.App.UiTests.Infrastructure;

namespace NoteManager.App.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("EndToEnd")]
internal sealed class VaultNavigationUiTests : UiTestBase
{
    [Test]
    public void RecursiveLoading_TagFilters_AndFullTextSearch_WorkTogether()
    {
        Vault.CreateStandardDataset();
        var app = Launch();

        UiScenario.WaitForNoteCount(app, 7);
        app.WaitForTextByAutomationId(
            "SearchIndexStatusText",
            "Full-text ready");
        Assert.That(
            File.Exists(Path.Combine(Vault.RootPath, ".notes", "search.db")),
            Is.True,
            "The per-vault full-text index was not created.");
        Assert.That(
            app.WaitForByAutomationId("SelectedNoteTitle").Name,
            Is.EqualTo("00 Current note.md"));
        Assert.That(
            app.WaitForByAutomationId("MarkdownEditor").AsTextBox().Text,
            Does.Contain("This is the initially selected note."));

        app.SelectListItem("NavigationList", "alpha");
        UiScenario.WaitForNoteCount(app, 2);
        app.WaitForTextByAutomationId("CenterHeadingText", "alpha");
        Assert.That(
            app.WaitForByAutomationId("SelectedNoteTitle").Name,
            Is.AnyOf("00 Current note.md", "02 Multiple tag blocks.md"));

        app.SelectListItem("NavigationList", "Untagged");
        UiScenario.WaitForNoteCount(app, 2);
        app.WaitForTextByAutomationId(
            "SelectedNoteTitle",
            "03 Untagged.md");

        app.SelectListItem("NavigationList", "All notes");
        UiScenario.WaitForNoteCount(app, 7);

        app.SetText("SearchBox", "quantum-needle-9281");
        UiScenario.WaitForNoteCount(app, 1);
        app.WaitForTextByAutomationId(
            "SelectedNoteTitle",
            "01 Nested search.md");
        Assert.That(
            app.WaitForByAutomationId("MarkdownEditor").AsTextBox().Text,
            Does.Contain("Folder discovery must search subfolders."));

        app.SetText("SearchBox", "zażółć gęślą");
        UiScenario.WaitForNoteCount(app, 1);
        app.WaitForTextByAutomationId(
            "SelectedNoteTitle",
            "05 Unicode.md");

        app.SetText("SearchBox", "missing-token-0000");
        UiScenario.WaitForNoteCount(app, 0);
    }
}
