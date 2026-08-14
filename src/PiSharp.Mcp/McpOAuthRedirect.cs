using System.Net;
using System.Text;
using ModelContextProtocol.Authentication;

namespace PiSharp.Mcp;

/// <summary>
/// Captures the MCP OAuth authorization-code callback on a loopback listener. The browser is
/// pointed at the authorization URL by <see cref="OAuthCredentialProvider"/>; the authorization
/// server redirects back to <c>redirectUri</c> with <c>code</c> and <c>state</c>, which this
/// helper returns to the SDK to exchange for tokens.
/// </summary>
internal static class McpOAuthRedirect
{
    /// <summary>Picks a free TCP port for the loopback redirect listener.</summary>
    public static int PickFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static async Task<AuthorizationResult> CaptureAsync(Uri redirectUri, CancellationToken cancellationToken)
    {
        if (redirectUri is null) throw new ArgumentNullException(nameof(redirectUri));
        if (!IsLoopback(redirectUri.Host))
            throw new InvalidOperationException($"OAuth redirect URI must be a loopback address; got '{redirectUri}'.");

        var prefix = $"{redirectUri.Scheme}://{redirectUri.Host}:{redirectUri.Port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        try
        {
            var context = await GetContextAsync(listener, cancellationToken);
            var query = context.Request.QueryString;
            var code = query["code"];
            var state = query["state"];
            var issuer = query["iss"];
            var error = query["error"];

            var body = error is null
                ? "<!DOCTYPE html><html><body><p>Authentication complete. You may close this window.</p></body></html>"
                : $"<!DOCTYPE html><html><body><p>Authentication failed: {WebUtility.HtmlEncode(error)}</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();

            cancellationToken.ThrowIfCancellationRequested();
            if (error is not null)
                throw new InvalidOperationException($"OAuth authorization failed: {error}");
            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException("OAuth callback did not include an authorization code.");
            return new AuthorizationResult { Code = code, State = state, Iss = issuer };
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsLoopback(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpListenerContext> GetContextAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        // HttpListener.GetContextAsync has no cancellation overload; race the task against the token.
        var getContext = listener.GetContextAsync();
        var completed = await Task.WhenAny(getContext, Task.Delay(Timeout.Infinite, cancellationToken));
        if (completed != getContext)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        return await getContext;
    }
}
