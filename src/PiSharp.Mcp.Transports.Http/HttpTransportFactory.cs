using System.Net.Http.Headers;
using ModelContextProtocol.Client;

namespace PiSharp.Mcp.Transports.Http;

/// <summary>
/// Creates <see cref="HttpClientTransport"/> instances for HTTP MCP servers (streamable HTTP by
/// default, legacy SSE via <c>httpMode: "sse"</c>). Credentials are applied per request through a
/// <see cref="DelegatingHandler"/> that consults the matching credential provider, so rotating
/// OAuth tokens refresh without re-wiring the transport; when no stored credentials exist and the
/// server uses OAuth, the SDK's interactive flow (discovery, dynamic registration, PKCE) is
/// configured instead.
/// </summary>
public sealed class HttpTransportFactory : IMcpTransportFactory
{
    public string Kind => "http";

    public bool CanCreate(McpServerConfig config)
        => config.Transport == McpTransportKind.Http;

    public ValueTask<IClientTransport> CreateAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || !Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("An http MCP server requires an absolute URL.");

        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = string.Equals(config.HttpMode, "sse", StringComparison.OrdinalIgnoreCase)
                ? HttpTransportMode.Sse
                : HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            Name = config.Name
        };

        if (config.Headers is { Count: > 0 })
        {
            options.AdditionalHeaders = new Dictionary<string, string>(config.Headers, StringComparer.OrdinalIgnoreCase);
        }

        var provider = McpCredentialResolver.Resolve(config);
        var credential = provider.ResolveAsync(config, context, cancellationToken).AsTask().GetAwaiter().GetResult();

        HttpClient httpClient;
        if (credential is not null)
        {
            httpClient = new HttpClient(new BearerDelegatingHandler(provider, config, context)
            {
                InnerHandler = new HttpClientHandler()
            });
        }
        else if (config.Auth?.Kind == McpAuthKind.OAuth && provider is OAuthCredentialProvider oauth)
        {
            // No stored credentials: let the SDK run the interactive OAuth flow on connect.
            oauth.ConfigureOAuth(options, config, context);
            httpClient = new HttpClient();
        }
        else
        {
            httpClient = new HttpClient();
        }

        return ValueTask.FromResult<IClientTransport>(
            new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: true));
    }
}

/// <summary>
/// Attaches a bearer token from the resolved credential to every request. Re-resolving per request
/// lets a refreshed token take effect without rebuilding the transport.
/// </summary>
internal sealed class BearerDelegatingHandler(
    IMcpCredentialProvider provider,
    McpServerConfig config,
    McpTransportContext context) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var credential = await provider.ResolveAsync(config, context, cancellationToken);
            if (credential is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        }
        catch (Exception ex)
        {
            context.Log($"MCP server '{config.Name}': credential resolution failed: {ex.Message}");
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
