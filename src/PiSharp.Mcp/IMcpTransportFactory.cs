using ModelContextProtocol.Client;

namespace PiSharp.Mcp;

/// <summary>
/// Creates an SDK transport for a server configuration. Concrete factories ship as separate
/// transport plugins (<c>PiSharp.Mcp.Transports.Stdio</c>, <c>PiSharp.Mcp.Transports.Http</c>) and
/// register themselves into <see cref="McpServerRegistry"/>; the host never constructs transports
/// directly. <see cref="IClientTransport"/> is the SDK boundary — everything above it in the host
/// is SDK-free.
/// </summary>
public interface IMcpTransportFactory
{
    /// <summary>Transport kind: "stdio" or "http".</summary>
    string Kind { get; }

    /// <summary>True when this factory can create a transport for <paramref name="config"/>.</summary>
    bool CanCreate(McpServerConfig config);

    ValueTask<IClientTransport> CreateAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken);
}
