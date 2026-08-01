using NoteManager.Desktop.UiTests.Infrastructure;
using NUnit.Framework;

namespace NoteManager.Desktop.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("UI")]
internal sealed class NoteTitleEditingUiTests : UiTestBase
{
    [Test]
    public void InlineTitleEditor_EnterAndLostFocusRenameTheFileAndResolveCollisions()
    {
        File.WriteAllText(Path.Combine(Vault.RootPath, "filename.md"), "existing");
        File.WriteAllText(Path.Combine(Vault.RootPath, "filename (1).md"), "existing");
        var app = Launch(expectedNoteCount: 9);

        app.RenameSelectedNoteAndPressEnter("filename.md");

        app.WaitForSelectedTitle("filename (2).md");
        Assert.That(
            File.Exists(Path.Combine(Vault.RootPath, "filename (2).md")),
            Is.True);

        app.RenameSelectedNoteAndLeaveEditor("focus saved");

        app.WaitForSelectedTitle("focus saved.md");
        Assert.That(
            File.Exists(Path.Combine(Vault.RootPath, "focus saved.md")),
            Is.True);
    }
}
