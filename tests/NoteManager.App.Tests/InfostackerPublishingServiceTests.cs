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
    public async Task PublishAsync_BarePdfWithUniqueVaultWideMatch_UploadsThePreviewedFile()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("notes/Report.md", "![[appendix.pdf]]");
        var expectedPath = vault.Write("assets/appendix.pdf", "the uniquely matched document");
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        // A unique nested basename follows the same shared policy in the editor, preview, and final upload.
        var editorReference = Assert.Single(MarkdownFolderService.LoadFolder(vault.Path).Notes
            .Single(note => note.SourceFilePath == notePath)
            .EmbeddedMediaReferences);
        var previewAttachment = Assert.Single(service.PreviewAttachments(
            CreateNote(notePath),
            vault.Path,
            File.ReadAllText(notePath)));
        await service.PublishAsync(CreateNote(notePath), vault.Path);

        Assert.Equal(expectedPath, editorReference.ResolvedPath);
        Assert.Equal(expectedPath, previewAttachment.ResolvedPath);
        Assert.True(previewAttachment.IsAvailable);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("the uniquely matched document", handler.RequestBody);
    }

    /// <summary>
    /// Confirms root-qualified and note-qualified links retain their authored
    /// precedence while using the same resolver in preview and publication.
    /// </summary>
    [Theory]
    [InlineData("notes/Report.md", "assets/root.pdf", "assets/root.pdf")]
    [InlineData("notes/Report.md", "./note.pdf", "notes/note.pdf")]
    public async Task PublishAsync_QualifiedPdfLink_UploadsTheFileSelectedByEditorAndPreview(
        string noteRelativePath,
        string target,
        string attachmentRelativePath)
    {
        using var vault = new TestVault();
        var notePath = vault.Write(noteRelativePath, $"![[{target}]]");
        var expectedPath = vault.Write(attachmentRelativePath, $"contents of {attachmentRelativePath}");
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        var editorReference = Assert.Single(MarkdownFolderService.LoadFolder(vault.Path).Notes
            .Single(note => note.SourceFilePath == notePath)
            .EmbeddedMediaReferences);
        var previewAttachment = Assert.Single(service.PreviewAttachments(
            CreateNote(notePath),
            vault.Path,
            File.ReadAllText(notePath)));
        await service.PublishAsync(CreateNote(notePath), vault.Path);

        Assert.Equal(expectedPath, editorReference.ResolvedPath);
        Assert.Equal(expectedPath, previewAttachment.ResolvedPath);
        Assert.True(previewAttachment.IsAvailable);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains($"contents of {attachmentRelativePath}", handler.RequestBody);
    }

    /// <summary>
    /// Ensures ambiguous bare basenames remain unavailable on every surface and
    /// give the author an explicit path correction instead of selecting a file.
    /// </summary>
    [Fact]
    public async Task PublishAsync_AmbiguousBarePdf_RejectsItWithAnExplicitPathCorrection()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "![[duplicate.pdf]]");
        vault.Write("assets/duplicate.pdf", "first");
        vault.Write("archive/duplicate.pdf", "second");
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        var editorReference = Assert.Single(MarkdownFolderService.LoadFolder(vault.Path).Notes
            .Single(note => note.SourceFilePath == notePath)
            .EmbeddedMediaReferences);
        var previewAttachment = Assert.Single(service.PreviewAttachments(
            CreateNote(notePath),
            vault.Path,
            File.ReadAllText(notePath)));
        var exception = await Assert.ThrowsAsync<InfostackerPublishingException>(
            () => service.PublishAsync(CreateNote(notePath), vault.Path));

        Assert.False(File.Exists(editorReference.ResolvedPath));
        Assert.False(previewAttachment.IsAvailable);
        Assert.Contains("ambiguous", previewAttachment.Issue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit vault-relative or note-relative path", exception.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>
    /// Prevents a relative traversal from previewing or publishing a PDF that
    /// lives outside the selected vault, with a correction shown to the author.
    /// </summary>
    [Fact]
    public async Task PublishAsync_OutsideVaultPdf_RejectsItWithAVaultBoundedCorrection()
    {
        using var vault = new TestVault();
        var outsideFileName = $"outside-{Guid.NewGuid():N}.pdf";
        var outsidePath = Path.Combine(Path.GetDirectoryName(vault.Path)!, outsideFileName);
        File.WriteAllText(outsidePath, "external document");
        try
        {
            var notePath = vault.Write("Report.md", $"![[../{outsideFileName}]]");
            var handler = new CapturingHandler();
            using var client = new HttpClient(handler);
            var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

            var editorReference = Assert.Single(MarkdownFolderService.LoadFolder(vault.Path).Notes
                .Single(note => note.SourceFilePath == notePath)
                .EmbeddedMediaReferences);
            var previewAttachment = Assert.Single(service.PreviewAttachments(
                CreateNote(notePath),
                vault.Path,
                File.ReadAllText(notePath)));
            var exception = await Assert.ThrowsAsync<InfostackerPublishingException>(
                () => service.PublishAsync(CreateNote(notePath), vault.Path));

            Assert.False(File.Exists(editorReference.ResolvedPath));
            Assert.False(previewAttachment.IsAvailable);
            Assert.Contains("outside the current folder", previewAttachment.Issue);
            Assert.Contains("move it into the folder", exception.Message);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            // The external fixture is deliberately separate from the vault, so clean it up explicitly after verification.
            File.Delete(outsidePath);
        }
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

    [Fact]
    public async Task PublishAsync_EncodedPdfFilenameWithFragmentAndAlias_UploadsTheLiteralPdf()
    {
        using var vault = new TestVault();
        const string pdfFileName = "report#[draft]%1.pdf";
        const string pdfContents = "the exact PDF content";
        var notePath = vault.Write(
            "Report.md",
            "![[report%23%5Bdraft%5D%251.pdf#page=3|Reader copy]]");
        vault.Write(pdfFileName, pdfContents);
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        var attachments = service.PreviewAttachments(
            CreateNote(notePath),
            vault.Path,
            File.ReadAllText(notePath));
        await service.PublishAsync(CreateNote(notePath), vault.Path);

        // Splitting markup before decode keeps the encoded # and brackets in the required PDF's filename.
        var attachment = Assert.Single(attachments);
        Assert.True(attachment.IsAvailable);
        Assert.True(attachment.IsPdf);
        Assert.Equal(pdfFileName, attachment.FileName);
        var requestBody = Assert.IsType<string>(handler.RequestBody);
        Assert.Contains(pdfFileName, requestBody);
        Assert.Contains(pdfContents, requestBody);
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
    public async Task PublishAsync_StalledResponseBody_TimesOutAfterHeaders()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Draft");
        var handler = new StallingResponseHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new InfostackerPublishingService(
            client,
            new Uri("https://example.test/"),
            TimeSpan.FromMilliseconds(50));

        // Headers are already available, so only the service-wide deadline can release this blocked body read.
        var exception = await Assert.ThrowsAsync<InfostackerPublishingException>(
            () => service.PublishAsync(CreateNote(notePath), vault.Path)
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PublishAsync_GrowingResponseBody_RejectsBodyLargerThanLimit()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Draft");
        var handler = new GrowingResponseHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new InfostackerPublishingService(client, new Uri("https://example.test/"));

        // The stream never ends and omits Content-Length, so byte counting must enforce the response limit.
        var exception = await Assert.ThrowsAsync<InfostackerPublishingException>(
            () => service.PublishAsync(CreateNote(notePath), vault.Path)
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("exceeds the 1 MB limit", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task PublishSelectedNoteAsync_StalledResponseBody_TimesOutAndRetainsEditorContent()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Initial draft");
        var handler = new StallingResponseHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var publishingService = new InfostackerPublishingService(
            client,
            new Uri("https://example.test/"),
            TimeSpan.FromMilliseconds(50));
        using var viewModel = new MainViewModel(publishingService, new ApplicationActivityLog(vault.Path));
        await viewModel.LoadMarkdownFolderAsync(vault.Path, notePath);
        await WaitForIndexAsync(viewModel);
        const string expectedContent = "# Updated draft\n\nContent that must remain available.";
        viewModel.SelectedNote!.PlainTextContent = expectedContent;

        // A bounded completion restores IsPublishing so the dialog can be closed without losing the editor draft.
        var publicUrl = await viewModel.PublishSelectedNoteAsync()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(publicUrl);
        Assert.False(viewModel.IsPublishing);
        Assert.Equal(expectedContent, viewModel.SelectedNote.PlainTextContent);
        Assert.Equal(expectedContent, File.ReadAllText(notePath));
        Assert.Contains("timed out", viewModel.ShareStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishSelectedNoteAsync_UserCancellation_IsNotReportedAsATimeout()
    {
        using var vault = new TestVault();
        var notePath = vault.Write("Report.md", "# Draft");
        var handler = new StallingResponseHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var publishingService = new InfostackerPublishingService(
            client,
            new Uri("https://example.test/"),
            TimeSpan.FromSeconds(10));
        using var viewModel = new MainViewModel(publishingService, new ApplicationActivityLog(vault.Path));
        await viewModel.LoadMarkdownFolderAsync(vault.Path, notePath);
        await WaitForIndexAsync(viewModel);

        var publishTask = viewModel.PublishSelectedNoteAsync();
        await handler.WaitForRequestAsync();
        // The dialog's Cancel action calls this method, so explicit cancellation must retain its distinct status.
        viewModel.CancelPublishing();
        var publicUrl = await publishTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(publicUrl);
        Assert.False(viewModel.IsPublishing);
        Assert.Equal("Publishing cancelled.", viewModel.ShareStatusText);
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

    private sealed class StallingResponseHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public Task WaitForRequestAsync() => _requestStarted.Task;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            // Return successful headers immediately while the stream waits for the service's linked cancellation token.
            _requestStarted.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            });
        }
    }

    private sealed class GrowingResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // No Content-Length models a peer that keeps appending data after valid response headers.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GrowingStream())
            });
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => WaitForCancellationAsync(cancellationToken);

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            // The artificial body produces no bytes and completes only when the linked operation deadline cancels it.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class GrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => WriteChunk(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => Task.FromResult(WriteChunk(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(WriteChunk(buffer.Span));

        private static int WriteChunk(Span<byte> buffer)
        {
            // Repeated non-JSON bytes model an indefinitely growing response without predeclaring its size.
            buffer.Fill((byte)'x');
            return buffer.Length;
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
