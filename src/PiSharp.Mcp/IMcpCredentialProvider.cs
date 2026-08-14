namespace PiSharp.Mcp;

/// <summary>
/// Resolves credentials for a server configuration. Returns null to signal anonymous access.
/// Concrete providers ship inside the host assembly: <see cref="EnvCredentialProvider"/>,
/// <see cref="LiteralCredentialProvider"/>, <see cref="OAuthCredentialProvider"/>.
/// </summary>
public interface IMcpCredentialProvider
{
    McpAuthKind Kind { get; }

    ValueTask<McpCredential?> ResolveAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken);
}
