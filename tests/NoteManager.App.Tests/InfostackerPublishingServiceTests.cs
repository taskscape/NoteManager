using System.Net;
using System.Net.Http;
using System.Text;
using NoteManager.App.Models;
using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Unit")]
public sealed class InfostackerPublishingServiceTests
{
    [Fact]
    public async Task PublishAsync_AppendsSiblingOriginalPdfToCurrentMarkdownAndUploadsIt()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Current edited Markdown");
        vault.Write("Report.pdf", "original pdf");
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        var publicUrl = await service.PublishAsync(CreateNote(notePath), vault.Path);

        Assert.Equal("https://example.test/sharing/public-note", publicUrl);
        Assert.NotNull(handler.RequestBody);
        Assert.Contains("# Current edited Markdown", handler.RequestBody);
        Assert.Contains("![[Report.pdf]]", handler.RequestBody);
        Assert.Contains("filename=Report.pdf", handler.RequestBody);
    }

    [Fact]
    public async Task PublishAsync_MissingExplicitPdf_BlocksBeforeSendingTheRequest()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Note\n\n![[missing.pdf]]");
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        var exception = await Assert.ThrowsAsync<InfostackerPublishingException>(
            () => service.PublishAsync(CreateNote(notePath), vault.Path));

        Assert.Contains("missing.pdf", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PublishAsync_ExplicitPdfDoesNotSelectSameNamedFileFromAnotherFolder()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("notes/Report.md", "![[appendix.pdf]]");
        vault.Write("unrelated/appendix.pdf", "wrong document");
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        await Assert.ThrowsAsync<InfostackerPublishingException>(
            () => service.PublishAsync(CreateNote(notePath), vault.Path));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void PreviewAttachments_ShowsImplicitOriginalPdfBeforePublishing()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Draft");
        vault.Write("Report.pdf", "original pdf");
        var service = new InfostackerPublishingService();

        var attachments = service.PreviewAttachments(
            CreateNote(notePath),
            vault.Path,
            "# Draft");

        var attachment = Assert.Single(attachments);
        Assert.True(attachment.IsAvailable);
        Assert.True(attachment.IsPdf);
        Assert.True(attachment.IsImplicit);
        Assert.Equal("Report.pdf", attachment.FileName);
    }

    private static NoteItem CreateNote(string path) => new()
    {
        Title = Path.GetFileName(path),
        Subtitle = "Markdown note",
        FileName = Path.GetFileName(path),
        Size = "0 B",
        Date = "01.01.2026",
        Notebook = "Test",
        ThumbnailKind = ThumbnailKind.Markdown,
        DocumentHeading = "Report",
        DocumentSubheading = path,
        Paragraphs = [],
        Tags = [],
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        SizeBytes = 0,
        ModifiedAt = "01.01.2026 00:00",
        IsMarkdownFile = true,
        SourceFilePath = path
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"public-note\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestVault : IDisposable
    {
        public TestVault()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"NoteManager.InfostackerTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string relativePath, string contents)
        {
            var path = System.IO.Path.Combine(
                Path,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
