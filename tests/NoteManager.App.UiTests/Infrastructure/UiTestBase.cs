using FlaUI.Core.Capturing;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace NoteManager.App.UiTests.Infrastructure;

internal abstract class UiTestBase
{
    private readonly List<IDisposable> _resources = [];

    protected DisposableNoteVault Vault { get; private set; } = null!;
    protected NoteManagerAppSession? Session { get; private set; }
    protected string ArtifactDirectory { get; private set; } = string.Empty;

    [SetUp]
    public void CreateDisposableWorkspace()
    {
        Vault = new DisposableNoteVault();
        ArtifactDirectory = Path.Combine(
            UiTestPaths.ArtifactsRoot,
            SanitizeFileName(TestContext.CurrentContext.Test.FullName));
        Directory.CreateDirectory(ArtifactDirectory);
    }

    [TearDown]
    public void DisposeDesktopResources()
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
            for (var index = _resources.Count - 1; index >= 0; index--)
            {
                _resources[index].Dispose();
            }

            _resources.Clear();
            Vault.Dispose();
        }
    }

    protected NoteManagerAppSession Launch(
        Uri? infostackerBaseUri = null,
        string? automationPipeName = null)
    {
        Session = NoteManagerAppSession.Launch(
            Vault.RootPath,
            infostackerBaseUri,
            automationPipeName);
        return Session;
    }

    protected T Register<T>(T resource)
        where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    private void CaptureFailureEvidence()
    {
        try
        {
            var screenPath = Path.Combine(ArtifactDirectory, "failure-screen.png");
            using (var image = Capture.Screen())
            {
                image.ToFile(screenPath);
            }

            TestContext.AddTestAttachment(
                screenPath,
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
