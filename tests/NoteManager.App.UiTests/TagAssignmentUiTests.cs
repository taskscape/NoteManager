using NUnit.Framework;
using NoteManager.App.UiTests.Infrastructure;

namespace NoteManager.App.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("UI")]
internal sealed class TagAssignmentUiTests : UiTestBase
{
    [Test]
    public void AssignTags_UsesRecentAndAllLists_ValidatesAndMergesBlocks()
    {
        Vault.CreateStandardDataset();
        Vault.AddTagCatalog(55);
        var app = Launch();
        UiScenario.WaitForNoteCount(app, 62);
        UiScenario.SelectNoteBySearch(app, "02 Multiple tag blocks.md");

        app.Invoke("TagsToolbarButton");
        var dialog = app.WaitForWindow("Assign Tags");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                app.WaitForByAutomationId("AssignTagsHeading", dialog).Name,
                Is.EqualTo(
                    "Assign tags to note: \"02 Multiple tag blocks.md\""));
            Assert.That(
                app.WaitForByAutomationId("TagListDescription", dialog).Name,
                Is.EqualTo(
                    "50 most recently used tags in this folder · 50 shown"));
            Assert.That(
                app.WaitForByAutomationId("SelectedTagCountText", dialog).Name,
                Is.EqualTo("4 tags selected"));
        }

        app.SetText("TagSearchBox", "alpha", dialog);
        var alpha = UiScenario.WaitForCheckBox(dialog, "alpha");
        Assert.That(alpha.IsChecked, Is.True);
        Assert.That(
            dialog.FindFirstDescendant(conditionFactory =>
                conditionFactory.ByName("ALPHA")),
            Is.Null,
            "Tag names must be displayed in lowercase.");

        app.SetText("NewTagsTextBox", "bad_tag", dialog);
        app.Invoke("AddTagsButton", dialog);
        UiWait.Until(
            () => app
                .WaitForByAutomationId("TagValidationMessage", dialog)
                .Name
                .Contains(
                    "unsupported characters",
                    StringComparison.OrdinalIgnoreCase),
            "the invalid-tag validation message");

        app.SetText("NewTagsTextBox", "New.Tag added-tag", dialog);
        app.Invoke("AddTagsButton", dialog);
        app.SetText("TagSearchBox", "second", dialog);
        var second = UiScenario.WaitForCheckBox(dialog, "second");
        second.IsChecked = false;

        app.Invoke("AllTagsButton", dialog);
        app.SetText("TagSearchBox", "catalog-54", dialog);
        Assert.That(
            UiScenario.WaitForCheckBox(dialog, "catalog-54"),
            Is.Not.Null);

        app.Invoke("AssignTagsOkButton", dialog);
        app.WaitForWindowToClose("Assign Tags");

        UiScenario.WaitForFileContent(
            Vault.MultiTagNotePath,
            markdown =>
                UiScenario.CountTagHeaders(markdown) == 1
                && markdown.Contains(
                    "  - added-tag",
                    StringComparison.Ordinal)
                && markdown.Contains(
                    "  - new.tag",
                    StringComparison.Ordinal)
                && !markdown.Contains(
                    "  - second",
                    StringComparison.OrdinalIgnoreCase)
                && !markdown.Contains(
                    "  - ALPHA",
                    StringComparison.Ordinal),
            "the accepted tag selection to merge and save");
    }
}
