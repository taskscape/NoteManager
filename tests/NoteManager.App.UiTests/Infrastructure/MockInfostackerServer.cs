using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NoteManager.App.UiTests.Infrastructure;

internal sealed record PublishedRequest(
    string HttpMethod,
    string RawUrl,
    string ContentType,
    string Body);

internal sealed class MockInfostackerServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<PublishedRequest> _request =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _listenerTask;
    private bool _disposed;

    public MockInfostackerServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.AbsoluteUri);
        _listener.Start();
        _listenerTask = Task.Run(ListenOnceAsync);
    }

    public Uri BaseUri { get; }
    public Task<PublishedRequest> Request => _request.Task;

    private async Task ListenOnceAsync()
    {
        try
        {
            var context = await _listener.GetContextAsync()
                .WaitAsync(_cancellation.Token);
            try
            {
                using var memory = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(
                    memory,
                    _cancellation.Token);
                var request = new PublishedRequest(
                    context.Request.HttpMethod,
                    context.Request.RawUrl ?? string.Empty,
                    context.Request.ContentType ?? string.Empty,
                    Encoding.Latin1.GetString(memory.ToArray()));
                _request.TrySetResult(request);

                var responseBytes = Encoding.UTF8.GetBytes(
                    """{"id":"public-note-123","secret":"secret-456"}""");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(
                    responseBytes,
                    _cancellation.Token);
            }
            finally
            {
                context.Response.Close();
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
            or HttpListenerException
            or ObjectDisposedException)
        {
            _request.TrySetCanceled();
        }
        catch (Exception exception)
        {
            _request.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            _listenerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The request task already carries the actionable server failure.
        }

        _cancellation.Dispose();
    }
}
