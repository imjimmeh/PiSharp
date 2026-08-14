using System.Net;
using System.Text;

namespace PiSharp.Research.Tests;

/// <summary>
/// Test double for <see cref="HttpMessageHandler"/>: records the requests it
/// receives and returns a scripted response (or throws a scripted exception).
/// Supports both sync and async responders. No unit test touches the network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly List<HttpRequestMessage> _requests = [];

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public StubHttpMessageHandler(HttpStatusCode status, string body, string contentType = "application/json")
        : this((_, _) =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        })
    {
    }

    /// <summary>Returns a 200 response with the given bytes and declared content length.</summary>
    public static StubHttpMessageHandler ForBytes(byte[] body, string contentType, long? contentLength = null)
        => new((_, _) =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            if (contentLength is not null)
            {
                content.Headers.ContentLength = contentLength;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

    /// <summary>Creates a handler that fails every request with the given exception.</summary>
    public static StubHttpMessageHandler Failing(Exception exception)
        => new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Creates a handler that waits until cancelled (timeout testing).</summary>
    public static StubHttpMessageHandler Hanging()
        => new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requests.Add(await SnapshotAsync(request, cancellationToken).ConfigureAwait(false));
        var response = await _responder(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage = request;
        return response;
    }

    /// <summary>
    /// Copies the request (headers and body) into a snapshot the test owns.
    /// HttpClient disposes the live request message once the response is
    /// produced, so tests must inspect the copy, not the original.
    /// </summary>
    private static async Task<HttpRequestMessage> SnapshotAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var snapshot = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            snapshot.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            snapshot.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                snapshot.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return snapshot;
    }
}
