namespace NoteManager.App.UiTests.Infrastructure;

internal static class UiTestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string BuildConfiguration
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                "NOTEMANAGER_UI_TEST_CONFIGURATION");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            return outputDirectory.Parent?.Name is { Length: > 0 } name
                ? name
                : "Debug";
        }
    }

    public static string ApplicationExecutable
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                "NOTEMANAGER_UI_TEST_APP");
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    RepositoryRoot,
                    "src",
                    "NoteManager.App",
                    "bin",
                    BuildConfiguration,
                    "net8.0-windows10.0.19041.0",
                    "NoteManager.exe")
                : Path.GetFullPath(configured);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Build NoteManager before running the UI tests, or set "
                    + "NOTEMANAGER_UI_TEST_APP.",
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

    public static string SamplePdfPath
        => Path.Combine(
            RepositoryRoot,
            "SampleNotes",
            "documents",
            "orbital-guide.pdf");

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
