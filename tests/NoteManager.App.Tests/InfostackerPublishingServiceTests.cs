using System.Net;
using System.Net.Http;
using System.Text;
using NoteManager.App.Models;
using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Unit")]
public sealed class InfostackerPublishingServiceTests
{
    // These valid JSON values violate the publishing response contract in distinct ways and must remain recoverable.
    public static TheoryData<string> InvalidPublishingResponses => new()
    {
        "[]",
        "null",
        "\"not-an-object\"",
        "42",
        "true",
        "{}",
        "{\"id\":42}",
        "{\"id\":\"\"}",
        "{"
    };

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
    public async Task PublishAsync_MovesExistingOriginalPdfEmbedAfterLaterEditsWithoutRewritingTheNote()
    {
        using var vault = new TestVault();
        const string source = "# Current Markdown\n\n![[Report.pdf]]\n\n## Later edits\n\nThese notes came later.";
        var notePath = vault.Write("Report.md", source);
        vault.Write("Report.pdf", "original pdf");
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        await service.PublishAsync(CreateNote(notePath), vault.Path);

        var published = Assert.IsType<string>(handler.RequestBody);
        var laterEditsIndex = published.IndexOf("These notes came later.", StringComparison.Ordinal);
        var originalEmbedIndex = published.IndexOf("![[Report.pdf]]", StringComparison.Ordinal);
        Assert.True(laterEditsIndex >= 0);
        Assert.True(originalEmbedIndex > laterEditsIndex);
        Assert.Equal(1, published.Split("![[Report.pdf]]", StringSplitOptions.None).Length - 1);
        Assert.Equal(source, File.ReadAllText(notePath));
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

    [Theory]
    [MemberData(nameof(InvalidPublishingResponses))]
    public async Task PublishAsync_InvalidPublishingResponse_ThrowsHandledPublishingException(string responseBody)
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Draft");
        var handler = new CapturingHandler(responseBody);
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        // Each malformed protocol payload must be converted to the recoverable exception consumed by the UI.
        var exception = await Assert.ThrowsAsync<InfostackerPublishingException>(
            () => service.PublishAsync(CreateNote(notePath), vault.Path));

        Assert.NotNull(exception);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PublishSelectedNoteAsync_InvalidPublishingResponse_IsHandledAndRetainsEditorContent()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Initial draft");
        var handler = new CapturingHandler("[]");
        using var client = new HttpClient(handler);
        var publishingService = new InfostackerPublishingService(client, new Uri("https://example.test/"));
        var activityLog = new ApplicationActivityLog(vault.Path);
        using var viewModel = new MainViewModel(publishingService, activityLog);
        await viewModel.LoadMarkdownFolderAsync(vault.Path, notePath);
        await WaitForIndexAsync(viewModel);
        const string expectedContent = "# Updated draft\n\nContent that must remain available.";
        viewModel.SelectedNote!.PlainTextContent = expectedContent;

        // A bad response must surface as a normal publishing failure, not discard the in-memory editor content.
        var publicUrl = await viewModel.PublishSelectedNoteAsync();

        Assert.Null(publicUrl);
        Assert.Equal(expectedContent, viewModel.SelectedNote.PlainTextContent);
        Assert.Equal(expectedContent, File.ReadAllText(notePath));
        Assert.Contains("invalid publishing response", viewModel.ShareStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsPublishing);
        // The view model must retain the protocol failure details for support without escalating it to the dispatcher.
        var logPath = Path.Combine(
            vault.Path,
            $"{ApplicationActivityLog.LogFilePrefix}{DateTime.Today:yyyy-MM-dd}.log");
        var logContents = File.ReadAllText(logPath);
        Assert.Contains("Operation failed (Publishing a public link):", logContents);
        Assert.Contains(nameof(InfostackerPublishingException), logContents);
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

    private static async Task WaitForIndexAsync(MainViewModel viewModel)
    {
        // Publishing schedules indexing after saving, so wait before the temporary vault is deleted.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (viewModel.IsIndexing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.False(viewModel.IsIndexing);
    }

    private sealed class CapturingHandler(string responseBody = "{\"id\":\"public-note\"}") : HttpMessageHandler
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
                // A configurable response body makes protocol-shape regressions testable without a live service.
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
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
            // Folder indexing can release SQLite's file handle just after the view model reports completion on Windows.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 19)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
