namespace NoteManager.Desktop.UiTests.Infrastructure;

internal static class UiTestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ApplicationExecutable
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                "NOTEMANAGER_UI_TEST_APP");
            var configuration = Environment.GetEnvironmentVariable(
                                    "NOTEMANAGER_UI_TEST_CONFIGURATION")
                                ?? "Debug";
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    RepositoryRoot,
                    "src",
                    "NoteManager.Desktop",
                    "bin",
                    configuration,
                    "net10.0",
                    "NoteManager.exe")
                : Path.GetFullPath(configured);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Build NoteManager.Desktop before running the UI tests, "
                    + "or set NOTEMANAGER_UI_TEST_APP.",
                    path);
            }

            return path;
        }
    }

    public static string ArtifactsRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                "NOTEMANAGER_UI_TEST_ARTIFACTS");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(RepositoryRoot, "artifacts", "ui-tests")
                : Path.GetFullPath(configured);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NoteManager.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate NoteManager.sln from the UI test output folder.");
    }
}
