using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-mcp-transports-http", Name = "MCP HTTP Transport", Version = "0.1.0",
    Description = "HTTP (streamable-http and legacy SSE) transport backend for the MCP client plugin.")]

namespace PiSharp.Mcp.Transports.Http;

/// <summary>Registers the http transport factory into <see cref="McpServerRegistry"/>.</summary>
public sealed class HttpTransportExtension : IExtension
{
    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        McpServerRegistry.RegisterFactory(new HttpTransportFactory());
        return Task.CompletedTask;
    }
}
