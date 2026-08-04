using System.Diagnostics;
using NoteManager.Plugin.GitIntegration;
using NoteManager.Plugins;
using Xunit;

namespace NoteManager.Plugin.GitIntegration.Tests;

[Trait("Category", "Integration")]
public sealed class GitSynchronizationServiceTests
{
    [Fact]
    public async Task SynchronizeAsync_SkipsWhenAnotherProcessOwnsTheVaultLock()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.GitLock.{Guid.NewGuid():N}"));
        var configurationDirectory = Directory.CreateDirectory(Path.Combine(
            root.FullName,
            ".note",
            "plugins",
            "git-integration")).FullName;

        try
        {
            await using var competingLock = new FileStream(
                Path.Combine(configurationDirectory, "synchronization.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var service = new GitSynchronizationService(
                new GitProcessRunner("git", TimeSpan.FromSeconds(30)),
                new GitSynchronizationLog(configurationDirectory));
            var context = new PluginHostContext(
                root.FullName,
                configurationDirectory,
                _ => Task.FromResult(true),
                _ => { });

            var result = await service.SynchronizeAsync(context);

            Assert.True(result.Skipped);
            Assert.Contains("Another NoteManager", result.Message);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SynchronizeAsync_ExcludesNewDotPathsButUpdatesTrackedDotPaths()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        using var repository = new DisposableGitRepository();
        repository.Initialize();
        File.WriteAllText(Path.Combine(repository.WorkingCopy, "note.md"), "updated note");
        Directory.CreateDirectory(Path.Combine(repository.WorkingCopy, "attachments"));
        File.WriteAllText(
            Path.Combine(repository.WorkingCopy, "attachments", "diagram.txt"),
            "diagram");
        Directory.CreateDirectory(Path.Combine(repository.WorkingCopy, ".note", "plugins"));
        File.WriteAllText(
            Path.Combine(repository.WorkingCopy, ".note", "plugins", "activated.json"),
            "local configuration");
        File.WriteAllText(
            Path.Combine(repository.WorkingCopy, ".obsidian", "workspace.json"),
            "updated tracked workspace");
        File.WriteAllText(
            Path.Combine(repository.WorkingCopy, ".obsidian", "new-local.json"),
            "new untracked workspace file");
        File.WriteAllText(
            Path.Combine(repository.WorkingCopy, ".env"),
            "TRACKED=updated");
        File.Delete(Path.Combine(repository.WorkingCopy, ".obsolete"));
        File.WriteAllText(
            Path.Combine(repository.WorkingCopy, ".new-hidden"),
            "new root dot file");
        Directory.CreateDirectory(Path.Combine(repository.WorkingCopy, "docs"));
        File.WriteAllText(
            Path.Combine(repository.WorkingCopy, "docs", ".draft.md"),
            "new nested dot file");

        var statusUpdates = new List<string>();
        var configurationDirectory = Path.Combine(
            repository.WorkingCopy,
            ".note",
            "plugins",
            "git-integration");
        var service = new GitSynchronizationService(
            new GitProcessRunner("git", TimeSpan.FromSeconds(30)),
            new GitSynchronizationLog(configurationDirectory));
        var context = new PluginHostContext(
            repository.WorkingCopy,
            configurationDirectory,
            _ => Task.FromResult(true),
            statusUpdates.Add);

        var result = await service.SynchronizeAsync(context);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(statusUpdates, status =>
            status.Contains("complete", StringComparison.OrdinalIgnoreCase));

        repository.CloneForVerification();
        Assert.Equal(
            "updated note",
            File.ReadAllText(Path.Combine(repository.VerificationCopy, "note.md")));
        Assert.True(File.Exists(Path.Combine(
            repository.VerificationCopy,
            "attachments",
            "diagram.txt")));
        Assert.False(Directory.Exists(Path.Combine(repository.VerificationCopy, ".note")));
        Assert.Equal(
            "updated tracked workspace",
            File.ReadAllText(Path.Combine(
                repository.VerificationCopy,
                ".obsidian",
                "workspace.json")));
        Assert.False(File.Exists(Path.Combine(
            repository.VerificationCopy,
            ".obsidian",
            "new-local.json")));
        Assert.Equal(
            "TRACKED=updated",
            File.ReadAllText(Path.Combine(repository.VerificationCopy, ".env")));
        Assert.False(File.Exists(Path.Combine(
            repository.VerificationCopy,
            ".obsolete")));
        Assert.False(File.Exists(Path.Combine(
            repository.VerificationCopy,
            ".new-hidden")));
        Assert.False(File.Exists(Path.Combine(
            repository.VerificationCopy,
            "docs",
            ".draft.md")));
    }

    [Fact]
    public async Task InspectAsync_RejectsARepositoryWithoutAnUpstream()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        using var repository = new DisposableGitRepository();
        RunGit(repository.Root, "init", repository.WorkingCopy);
        RunGit(repository.WorkingCopy, "config", "user.name", "NoteManager Tests");
        RunGit(repository.WorkingCopy, "config", "user.email", "tests@example.invalid");
        File.WriteAllText(Path.Combine(repository.WorkingCopy, "note.md"), "note");
        RunGit(repository.WorkingCopy, "add", "note.md");
        RunGit(repository.WorkingCopy, "commit", "-m", "Initial");
        var inspector = new GitRepositoryInspector(
            new GitProcessRunner("git", TimeSpan.FromSeconds(30)));

        var state = await inspector.InspectAsync(repository.WorkingCopy);

        Assert.False(state.CanSynchronize);
        Assert.Contains("upstream", state.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GitIsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start Git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {output} {error}");
    }

    private sealed class DisposableGitRepository : IDisposable
    {
        public DisposableGitRepository()
        {
            Root = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(),
                $"NoteManager.GitIntegration.{Guid.NewGuid():N}")).FullName;
            BareRemote = Path.Combine(Root, "remote.git");
            WorkingCopy = Path.Combine(Root, "working");
            VerificationCopy = Path.Combine(Root, "verification");
        }

        public string Root { get; }

        public string BareRemote { get; }

        public string WorkingCopy { get; }

        public string VerificationCopy { get; }

        public void Initialize()
        {
            RunGit(Root, "init", "--bare", BareRemote);
            RunGit(Root, "clone", BareRemote, WorkingCopy);
            RunGit(WorkingCopy, "config", "user.name", "NoteManager Tests");
            RunGit(WorkingCopy, "config", "user.email", "tests@example.invalid");
            File.WriteAllText(Path.Combine(WorkingCopy, "note.md"), "initial note");
            Directory.CreateDirectory(Path.Combine(WorkingCopy, ".obsidian"));
            File.WriteAllText(
                Path.Combine(WorkingCopy, ".obsidian", "workspace.json"),
                "initial tracked workspace");
            File.WriteAllText(
                Path.Combine(WorkingCopy, ".env"),
                "TRACKED=initial");
            File.WriteAllText(
                Path.Combine(WorkingCopy, ".obsolete"),
                "tracked file to delete");
            RunGit(
                WorkingCopy,
                "add",
                "note.md",
                ".obsidian/workspace.json",
                ".env",
                ".obsolete");
            RunGit(WorkingCopy, "commit", "-m", "Initial");
            RunGit(WorkingCopy, "push", "--set-upstream", "origin", "HEAD");
        }

        public void CloneForVerification()
            => RunGit(Root, "clone", BareRemote, VerificationCopy);

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        ClearReadOnlyAttributes(Root);
                        Directory.Delete(Root, recursive: true);
                    }

                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
            }
        }

        private static void ClearReadOnlyAttributes(string root)
        {
            foreach (var path in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            foreach (var path in Directory
                         .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                File.SetAttributes(path, FileAttributes.Directory);
            }
        }
    }
}
