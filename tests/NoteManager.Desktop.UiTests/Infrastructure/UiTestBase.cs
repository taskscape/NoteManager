using FlaUI.Core.Capturing;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace NoteManager.Desktop.UiTests.Infrastructure;

internal abstract class UiTestBase
{
    protected SearchTestVault Vault { get; private set; } = null!;
    protected NoteManagerAppSession? Session { get; private set; }
    protected string ArtifactDirectory { get; private set; } = string.Empty;

    [SetUp]
    public void SetUp()
    {
        Vault = new SearchTestVault();
        Vault.CreateDataset();
        ArtifactDirectory = Path.Combine(
            UiTestPaths.ArtifactsRoot,
            SanitizeFileName(TestContext.CurrentContext.Test.FullName));
        Directory.CreateDirectory(ArtifactDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (TestContext.CurrentContext.Result.Outcome.Status
                == TestStatus.Failed)
            {
                CaptureFailureEvidence();
            }
        }
        finally
        {
            Session?.Dispose();
            Session = null;
            Vault.Dispose();
        }
    }

    protected NoteManagerAppSession Launch(
        int expectedNoteCount = 7,
        bool waitForIndex = true)
    {
        Session = NoteManagerAppSession.Launch(Vault.RootPath);
        Session.WaitForNoteCount(expectedNoteCount);
        if (waitForIndex)
        {
            Session.WaitForIndexReady();
        }

        return Session;
    }

    private void CaptureFailureEvidence()
    {
        try
        {
            var screenshotPath = Path.Combine(
                ArtifactDirectory,
                "failure-screen.png");
            using var image = Capture.Screen();
            image.ToFile(screenshotPath);
            TestContext.AddTestAttachment(
                screenshotPath,
                "Desktop at failure");
        }
        catch (Exception exception)
        {
            TestContext.Progress.WriteLine(
                $"Could not capture the failure screen: {exception.Message}");
        }

        if (Session is null)
        {
            return;
        }

        try
        {
            var treePath = Path.Combine(
                ArtifactDirectory,
                "automation-tree.txt");
            File.WriteAllText(treePath, Session.DumpAutomationTree());
            TestContext.AddTestAttachment(
                treePath,
                "UI Automation tree at failure");
        }
        catch (Exception exception)
        {
            TestContext.Progress.WriteLine(
                $"Could not capture the automation tree: {exception.Message}");
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(
            value
                .Select(character =>
                    invalid.Contains(character) ? '_' : character)
                .ToArray());
        return sanitized.Length <= 100
            ? sanitized
            : sanitized[..100];
    }
}
