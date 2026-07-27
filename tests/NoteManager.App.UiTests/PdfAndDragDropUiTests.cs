using FlaUI.Core.Definitions;
using NUnit.Framework;
using NoteManager.App.UiTests.Infrastructure;

namespace NoteManager.App.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("UI")]
internal sealed class PdfAndDragDropUiTests : UiTestBase
{
    [Test]
    public void MultiplePdfTransclusions_CreateSeparateInteractiveViewers()
    {
        Vault.CreateStandardDataset();
        var app = Launch();
        UiScenario.WaitForNoteCount(app, 7);
        UiScenario.SelectNoteBySearch(app, "04 Multiple PDFs.md");

        Assert.That(
            app.WaitForByAutomationId("MarkdownEditor").AsTextBox().Text,
            Does.Contain("![[Documents/guide.pdf]]")
                .And.Contain("![[Documents/appendix.pdf]]"));
        UiWait.Until(
            () => app
                .WaitForByAutomationId("EmbeddedPdfViewers")
                .FindAllDescendants(conditionFactory =>
                    conditionFactory.ByControlType(ControlType.DataItem))
                .Length == 2,
            "two embedded PDF viewers");
        var embeddedPaths = app
            .WaitForByAutomationId("EmbeddedPdfViewers")
            .FindAllDescendants(conditionFactory =>
                conditionFactory.ByControlType(ControlType.DataItem))
            .Select(element => element.Name)
            .ToArray();
        Assert.That(
            embeddedPaths.Select(Path.GetFileName),
            Is.EquivalentTo(new[] { "guide.pdf", "appendix.pdf" }));
        app.WaitForByName(
            "guide.pdf - Web content",
            controlType: ControlType.Pane);
        app.WaitForByName(
            "appendix.pdf - Web content",
            controlType: ControlType.Pane);
    }

    [Test]
    public async Task InjectedExternalPdfDrop_CopiesRenamesEmbedsAndSavesTheNote()
    {
        Vault.CreateStandardDataset();
        File.Copy(
            UiTestPaths.SamplePdfPath,
            Path.Combine(Vault.RootPath, "guide.pdf"));
        var externalPdf = Vault.CreateExternalPdf("guide.pdf");
        var expectedCopy = Path.Combine(Vault.RootPath, "guide (1).pdf");
        var pipeName = $"NoteManager.UiTests.{Guid.NewGuid():N}";
        var app = Launch(automationPipeName: pipeName);
        UiScenario.WaitForNoteCount(app, 7);

        await app.ImportPdfAsync(pipeName, externalPdf);

        UiScenario.WaitForFileContent(
            Vault.CurrentNotePath,
            markdown => markdown.Contains(
                "![[guide (1).pdf]]",
                StringComparison.Ordinal),
            "the dropped PDF embed to save");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(expectedCopy), Is.True);
            Assert.That(
                File.ReadAllBytes(expectedCopy),
                Is.EqualTo(File.ReadAllBytes(externalPdf)));
        }
        UiWait.Until(
            () => app
                .WaitForByAutomationId("EmbeddedPdfViewers")
                .FindAllDescendants(conditionFactory =>
                    conditionFactory.ByControlType(ControlType.DataItem))
                .Length == 2,
            "the original and dropped PDF viewers");
    }
}
