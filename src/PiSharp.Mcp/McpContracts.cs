using System.Text.RegularExpressions;
using PiSharp.Ai.Auth;

namespace PiSharp.Mcp;

/// <summary>Which transport backend an MCP server configuration uses.</summary>
public enum McpTransportKind { Stdio, Http }

/// <summary>How credentials are resolved for an MCP server.</summary>
public enum McpAuthKind { None, Env, Literal, OAuth }

/// <summary>
/// Per-server authentication configuration. <see cref="McpAuthKind.Env"/> reads a bearer token from
/// the process environment, <see cref="McpAuthKind.Literal"/> uses a static token, and
/// <see cref="McpAuthKind.OAuth"/> runs the MCP OAuth flow with credentials persisted in the shared
/// auth store under <c>mcp:&lt;name&gt;</c>.
/// </summary>
public sealed record McpAuthConfig(
    McpAuthKind Kind,
    string? EnvVar = null,
    string? LiteralToken = null,
    string? ClientId = null)
{
    public static McpAuthConfig None { get; } = new(McpAuthKind.None);

    public bool IsValid(out string error)
    {
        switch (Kind)
        {
            case McpAuthKind.Env when string.IsNullOrWhiteSpace(EnvVar):
                error = "auth.envVar is required when auth.type is 'env'.";
                return false;
            case McpAuthKind.Literal when string.IsNullOrWhiteSpace(LiteralToken):
                error = "auth.token is required when auth.type is 'literal'.";
                return false;
            default:
                error = string.Empty;
                return true;
        }
    }
}

/// <summary>
/// Configuration for one MCP server. Constructed from the <c>mcpServers.&lt;name&gt;</c> settings
/// map (flattened keys) or contributed by another extension via <see cref="McpServerRegistry"/>.
/// </summary>
public sealed record McpServerConfig(
    string Name,
    string Source,
    McpTransportKind Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Cwd = null,
    string? Url = null,
    string HttpMode = "streamable-http",
    IReadOnlyDictionary<string, string>? Headers = null,
    McpAuthConfig? Auth = null,
    bool Enabled = true)
{
    private static readonly Regex NamePattern = new("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant);

    /// <summary>Normalizes a server name: lowercase, non [a-z0-9-] to '-', trimmed.</summary>
    public static string NormalizeName(string name)
    {
        var normalized = string.Concat((name ?? string.Empty).Trim().ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' ? ch : '-'));
        return normalized.Trim('-');
    }

    /// <summary>
    /// Validates the configuration. An invalid configuration becomes a per-server error state —
    /// it never fails the host.
    /// </summary>
    public bool Validate(out string error)
    {
        // A disabled server is never started, so its configuration cannot fail the host.
        if (!Enabled)
        {
            error = string.Empty;
            return true;
        }

        if (!NamePattern.IsMatch(Name))
        {
            error = $"Invalid MCP server name '{Name}': must match ^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$.";
            return false;
        }

        if (Transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(Command))
        {
            error = $"MCP server '{Name}': 'command' is required for stdio transport.";
            return false;
        }

        if (Transport == McpTransportKind.Http)
        {
            if (string.IsNullOrWhiteSpace(Url) || !Uri.TryCreate(Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = $"MCP server '{Name}': 'url' must be an absolute http(s) URL for http transport.";
                return false;
            }

            if (HttpMode != "streamable-http" && HttpMode != "sse")
            {
                error = $"MCP server '{Name}': 'httpMode' must be \"streamable-http\" or \"sse\".";
                return false;
            }
        }

        if (Auth is not null && !Auth.IsValid(out var authError))
        {
            error = $"MCP server '{Name}': {authError}";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Resolved credential applied to an MCP transport. <see cref="IMcpCredentialProvider.ResolveAsync"/>
/// returns null to signal anonymous access.
/// </summary>
public sealed record McpCredential(string AccessToken, string? RefreshToken = null, long? ExpiresUnixSeconds = null);

/// <summary>
/// Runtime services the host hands to transport factories and credential providers. The host
/// resolves the auth storage through a small seam so the planned C1 binding exposure
/// (<c>ExtensionRuntimeBinding.RuntimeAuthStorage</c>, owned by Spine-1) can slot in without
/// changing the plugin's public contracts.
/// </summary>
public sealed record McpTransportContext(
    IOAuthStorage? AuthStorage,
    Func<string, CancellationToken, Task> OpenUrlAsync,
    Action<string> Log)
{
    public static McpTransportContext Create(
        IOAuthStorage? authStorage,
        Func<string, CancellationToken, Task>? openUrlAsync = null,
        Action<string>? log = null)
        => new(authStorage, openUrlAsync ?? McpBrowserLauncher.OpenAsync, log ?? (_ => { }));
}

/// <summary>
/// Live status snapshot for one MCP server. <see cref="State"/> is one of
/// "connected", "connecting", "reconnecting", "disconnected", "error".
/// </summary>
public sealed record McpServerStatus(
    string Name,
    string Source,
    string State,
    int ToolCount = 0,
    string? LastError = null,
    string? ServerInfo = null,
    int? ReconnectAttempt = null);
