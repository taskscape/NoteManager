using System.Windows;
using NUnit.Framework;
using NoteManager.App.UiTests.Infrastructure;

namespace NoteManager.App.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("EndToEnd")]
internal sealed class PublishingUiTests : UiTestBase
{
    [Test]
    public async Task SharePublishesMultipartContent_AndCopiesPublicUrl()
    {
        Vault.CreatePublishingDataset();
        var server = Register(new MockInfostackerServer());
        var pipeName = $"NoteManager.UiTests.{Guid.NewGuid():N}";
        var app = Launch(server.BaseUri, pipeName);
        UiScenario.WaitForNoteCount(app, 1);

        Assert.That(
            app.WaitForByAutomationId("ShareToolbarButton").IsEnabled,
            Is.True);
        await app.OpenSharePanelAsync(pipeName);
        Assert.That(
            app.WaitForByAutomationId("CloseSharePanelButton"),
            Is.Not.Null);
        Assert.That(
            app.WaitForByAutomationId("PublishPublicLinkButton"),
            Is.Not.Null);
        Assert.That(
            app.MainWindow.FindFirstDescendant(conditionFactory =>
                conditionFactory.ByName("People with access")),
            Is.Null,
            "The public-link panel must not expose an access list.");

        app.Invoke("PublishPublicLinkButton");
        var request = await server.Request.WaitAsync(
            UiWait.DefaultTimeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.HttpMethod, Is.EqualTo("POST"));
            Assert.That(
                request.RawUrl,
                Is.EqualTo("/sharing/uploadmarkdownwithfiles"));
            Assert.That(
                request.ContentType,
                Does.StartWith("multipart/form-data"));
            Assert.That(request.Body, Does.Contain("Published note"));
            Assert.That(request.Body, Does.Contain("# Published body"));
            Assert.That(request.Body, Does.Contain("sample.txt"));
            Assert.That(
                request.Body,
                Does.Contain("embedded attachment payload"));
        }

        app.WaitForTextByAutomationId(
            "ShareStatusText",
            "Public link copied to the clipboard.");
        var expectedUrl =
            new Uri(server.BaseUri, "sharing/public-note-123").AbsoluteUri;
        UiWait.Until(
            () =>
            {
                try
                {
                    return Clipboard.ContainsText()
                           && Clipboard.GetText().Equals(
                               expectedUrl,
                               StringComparison.Ordinal);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    return false;
                }
            },
            "the published URL to reach the clipboard");
    }
}
