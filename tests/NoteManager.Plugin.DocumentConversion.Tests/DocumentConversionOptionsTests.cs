using System.Text.Json;
using NoteManager.Plugin.DocumentConversion;
using Xunit;

namespace NoteManager.Plugin.DocumentConversion.Tests;

public sealed class DocumentConversionOptionsTests
{
    [Fact]
    public void LoadOrCreate_WritesFiveMinuteLocalPdfDefaults()
    {
        using var folder = new TemporaryFolder();

        var options = DocumentConversionOptions.LoadOrCreate(folder.Path);

        Assert.True(options.Enabled);
        Assert.True(options.Recursive);
        Assert.Equal(5, options.IntervalMinutes);
        Assert.Equal("local", options.PdfProcessing);
        Assert.Equal("eng+pol", options.OcrLanguages);
        using var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(folder.Path, "settings.json")));
        Assert.Equal(
            5,
            settings.RootElement.GetProperty("IntervalMinutes").GetInt32());
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"NoteManager.DocumentConversion.{Guid.NewGuid():N}"))
                .FullName;
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
