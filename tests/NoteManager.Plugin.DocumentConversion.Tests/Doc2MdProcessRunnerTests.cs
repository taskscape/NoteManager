using Xunit;

namespace NoteManager.Plugin.DocumentConversion.Tests;

[Trait("Category", "Contract")]
public sealed class Doc2MdProcessRunnerTests
{
    [Fact]
    public void BuildArguments_UsesSingleFileLocalPdfDefaultsWithoutOverwrite()
    {
        var options = new DocumentConversionOptions();
        var runner = new Doc2MdProcessRunner("DOC2MD.Cli.exe", options);

        var arguments = runner.BuildArguments(
            @"C:\Notes Folder\source.pdf",
            @"C:\Notes Folder\source.md");

        Assert.Equal(
            [
                "convert",
                "--input",
                @"C:\Notes Folder\source.pdf",
                "--output",
                @"C:\Notes Folder\source.md",
                "--json",
                "--pdf-processing",
                "local",
                "--ocr-languages",
                "eng+pol"
            ],
            arguments);
        Assert.DoesNotContain("--overwrite", arguments);
        Assert.DoesNotContain("convert-folder", arguments);
    }

    [Fact]
    public async Task ConvertFileAsync_ReturnsInstallationGuidanceWhenCliIsMissing()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-{Guid.NewGuid():N}",
            "DOC2MD.Cli.exe");
        var runner = new Doc2MdProcessRunner(
            missingPath,
            new DocumentConversionOptions());

        var result = await runner.ConvertFileAsync("source.pdf", "source.md");

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(missingPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reinstall DOC2MD", result.StandardError, StringComparison.Ordinal);
    }
}
