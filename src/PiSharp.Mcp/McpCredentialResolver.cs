namespace PiSharp.Mcp;

/// <summary>
/// Chooses the credential provider for a server config by <see cref="McpAuthKind"/>. A server
/// whose auth type is <c>none</c> still resolves through the OAuth provider so previously stored
/// credentials (<c>mcp:&lt;name&gt;</c>) keep working once present.
/// </summary>
public static class McpCredentialResolver
{
    public static IMcpCredentialProvider Resolve(McpServerConfig config)
        => config.Auth?.Kind switch
        {
            McpAuthKind.Env => new EnvCredentialProvider(),
            McpAuthKind.Literal => new LiteralCredentialProvider(),
            _ => new OAuthCredentialProvider()
        };
}
