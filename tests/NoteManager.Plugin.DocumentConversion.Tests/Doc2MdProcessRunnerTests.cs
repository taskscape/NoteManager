using NoteManager.Plugin.DocumentConversion;
using Xunit;

namespace NoteManager.Plugin.DocumentConversion.Tests;

public sealed class Doc2MdProcessRunnerTests
{
    [Fact]
    public void BuildArguments_UsesRecursiveLocalPdfDefaultsWithoutOverwrite()
    {
        var options = new DocumentConversionOptions();
        var runner = new Doc2MdProcessRunner("DOC2MD.Cli.exe", options);

        var arguments = runner.BuildArguments(@"C:\Notes Folder");

        Assert.Equal(
            [
                "convert-folder",
                "--input",
                @"C:\Notes Folder",
                "--recursive",
                "--continue-on-error",
                "--json",
                "--pdf-processing",
                "local",
                "--ocr-languages",
                "eng+pol"
            ],
            arguments);
        Assert.DoesNotContain("--overwrite", arguments);
    }
}
