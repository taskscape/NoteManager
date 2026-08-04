using FlaUI.Core.AutomationElements;
using NoteManager.Desktop.UiTests.Infrastructure;
using NUnit.Framework;

namespace NoteManager.Desktop.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("EndToEnd")]
[NonParallelizable]
internal sealed class PluginUiTests : UiTestBase
{
    [Test]
    public void PluginsDialog_ListsGitIntegrationAndPersistsActivationInTheVault()
    {
        var app = Launch();

        var toggle = app.OpenPluginsDialog("Git Integration");
        Assert.That(
            app.WaitForDesktopByName("Git Integration"),
            Is.Not.Null);
        Assert.That(
            app.WaitForDesktopByName("Document Conversion"),
            Is.Not.Null);
        Assert.That(toggle.IsChecked, Is.False);

        toggle.Toggle();

        var activationPath = Path.Combine(
            Vault.RootPath,
            ".note",
            "plugins",
            "activated.json");
        UiWait.Until(
            () => File.Exists(activationPath)
                  && File.ReadAllText(activationPath).Contains(
                      "git-integration",
                      StringComparison.Ordinal),
            "Git Integration activation to be stored in the vault");
        Assert.That(
            Directory.Exists(Path.Combine(
                Vault.RootPath,
                ".note",
                "plugins",
                "git-integration")),
            Is.True);
        app.WaitForDesktopByAutomationId("ClosePluginsButton").AsButton().Invoke();
    }
}
