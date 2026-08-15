using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using System.Runtime.InteropServices;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Unit")]
public sealed class StartupDataTests
{
    [Fact]
    public void ApplicationActivityLog_RecordsStartupAndFolderSources()
    {
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.ApplicationActivityLogTests.{Guid.NewGuid():N}");
        var folder = Directory.CreateDirectory(Path.Combine(logDirectory, "vault"));

        try
        {
            var log = new ApplicationActivityLog(logDirectory);

            Assert.True(log.TryWriteApplicationOpened());
            Assert.True(log.TryWriteFolderSelected(folder.FullName));
            Assert.True(log.TryWriteFolderRestoredFromPreviousSession(folder.FullName));

            var logPath = Path.Combine(
                logDirectory,
                $"{ApplicationActivityLog.LogFilePrefix}{DateTime.Today:yyyy-MM-dd}.log");
            var lines = File.ReadAllLines(logPath);

            Assert.Contains(lines, line => line.EndsWith("Application opened."));
            Assert.Contains(lines, line => line.EndsWith(
                $"Repository folder selected: {Path.GetFullPath(folder.FullName)}"));
            Assert.Contains(lines, line => line.EndsWith(
                $"Repository folder opened from previous session: {Path.GetFullPath(folder.FullName)}"));
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplicationActivityLog_RecordsUnhandledExceptionTypeMessageAndStack()
    {
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.ApplicationActivityLogCrash.{Guid.NewGuid():N}");

        try
        {
            Exception caught;
            try
            {
                throw new InvalidOperationException(
                    "preview failed",
                    new COMException("WebView unavailable", unchecked((int)0x80004005)));
            }
            catch (InvalidOperationException exception)
            {
                caught = exception;
            }

            Assert.True(new ApplicationActivityLog(logDirectory)
                .TryWriteUnhandledException("UI dispatcher", caught));

            var text = File.ReadAllText(Path.Combine(
                logDirectory,
                $"{ApplicationActivityLog.LogFilePrefix}{DateTime.Today:yyyy-MM-dd}.log"));
            Assert.Contains("Unhandled exception (UI dispatcher):", text);
            Assert.Contains("System.InvalidOperationException: preview failed", text);
            Assert.Contains("System.Runtime.InteropServices.COMException", text);
            Assert.Contains("WebView unavailable", text);
            Assert.Contains(
                nameof(ApplicationActivityLog_RecordsUnhandledExceptionTypeMessageAndStack),
                text);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplicationActivityLog_TruncatesOversizedCrashText()
    {
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.ApplicationActivityLogCrashTrim.{Guid.NewGuid():N}");

        try
        {
            var exception = new InvalidOperationException(new string('x', 40_000));
            Assert.True(new ApplicationActivityLog(logDirectory)
                .TryWriteUnhandledException("folder open", exception));

            var text = File.ReadAllText(Path.Combine(
                logDirectory,
                $"{ApplicationActivityLog.LogFilePrefix}{DateTime.Today:yyyy-MM-dd}.log"));
            Assert.Contains("Unhandled exception (folder open):", text);
            Assert.Contains("… truncated.", text);
            Assert.True(text.Length < 40_000 + 512);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplicationActivityLog_RecordsNonExceptionCrashObjects()
    {
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.ApplicationActivityLogCrashObject.{Guid.NewGuid():N}");

        try
        {
            Assert.True(new ApplicationActivityLog(logDirectory)
                .TryWriteUnhandledException(
                    "AppDomain.UnhandledException (terminating)",
                    "native failure"));

            var text = File.ReadAllText(Path.Combine(
                logDirectory,
                $"{ApplicationActivityLog.LogFilePrefix}{DateTime.Today:yyyy-MM-dd}.log"));
            Assert.Contains(
                "Unhandled exception (AppDomain.UnhandledException (terminating)): native failure",
                text);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplicationActivityLog_RemovesLogsOlderThanTwelveMonths()
    {
        var logDirectory = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.ApplicationActivityLogRetention.{Guid.NewGuid():N}"));
        var expiredLog = Path.Combine(
            logDirectory.FullName,
            $"{ApplicationActivityLog.LogFilePrefix}{DateTime.Today.AddMonths(-13):yyyy-MM-dd}.log");

        try
        {
            File.WriteAllText(expiredLog, "old");
            Assert.True(new ApplicationActivityLog(logDirectory.FullName)
                .TryWriteApplicationOpened());
            Assert.False(File.Exists(expiredLog));
        }
        finally
        {
            if (Directory.Exists(logDirectory.FullName))
            {
                Directory.Delete(logDirectory.FullName, recursive: true);
            }
        }
    }

    [Fact]
    public void DefaultViewModel_HasNoSampleNotesBeforeAFolderIsOpened()
    {
        using var viewModel = new MainViewModel();

        Assert.Empty(viewModel.NotesView);
        Assert.Null(viewModel.SelectedNote);
        Assert.False(viewModel.IsFolderMode);
    }

    [Fact]
    public void SampleData_RemainsAvailableThroughTheExplicitTestFactory()
    {
        using var viewModel = MainViewModel.CreateWithSampleDataForTesting();

        Assert.NotEmpty(viewModel.NotesView);
        Assert.NotNull(viewModel.SelectedNote);
        Assert.False(viewModel.IsFolderMode);
    }

    [Fact]
    public void LastOpenedFolder_IsPersistedAndReadOnlyWhenItStillExists()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.StartupDataTests.{Guid.NewGuid():N}"));
        var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "vault"));
        var statePath = Path.Combine(root.FullName, "state", "last-folder.txt");

        try
        {
            var writer = new LastOpenedFolderService(statePath);
            Assert.True(writer.TrySave(folder.FullName));

            var reader = new LastOpenedFolderService(statePath);
            Assert.Equal(Path.GetFullPath(folder.FullName), reader.ReadExistingFolder());

            Directory.Delete(folder.FullName);
            Assert.Null(reader.ReadExistingFolder());
        }
        finally
        {
            if (Directory.Exists(root.FullName))
            {
                Directory.Delete(root.FullName, recursive: true);
            }
        }
    }
}
