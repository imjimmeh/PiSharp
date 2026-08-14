using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-mcp-transports-stdio", Name = "MCP Stdio Transport", Version = "0.1.0",
    Description = "Stdio transport backend for the MCP client plugin.")]

namespace PiSharp.Mcp.Transports.Stdio;

/// <summary>Registers the stdio transport factory into <see cref="McpServerRegistry"/>.</summary>
public sealed class StdioTransportExtension : IExtension
{
    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        McpServerRegistry.RegisterFactory(new StdioTransportFactory());
        return Task.CompletedTask;
    }
}
