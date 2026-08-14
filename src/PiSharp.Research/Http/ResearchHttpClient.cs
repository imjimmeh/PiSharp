using System.Net.Http.Headers;

namespace PiSharp.Research.Http;

/// <summary>
/// Shared HTTP client factory for the research plugin's URL reading (arxiv,
/// PDF URLs, generic web pages). The underlying <see cref="HttpClient"/> is
/// created once and reused across calls; the timeout is bounded from settings
/// (<c>extensions.pisharp-research.fetch.timeoutSeconds</c>). Tests inject a
/// stub <see cref="HttpMessageHandler"/> so no unit test touches the network.
/// </summary>
public sealed class ResearchHttpClient : IDisposable
{
    private readonly HttpClient _client;

    public ResearchHttpClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _client.Timeout = timeout;
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PiSharp-Research", "1.0"));
    }

    public HttpClient Client => _client;

    public void Dispose() => _client.Dispose();
}
