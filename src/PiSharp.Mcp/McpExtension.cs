using PiSharp.Ai.Auth;
using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-mcp", Name = "MCP Client", Version = "0.1.0",
    Description = "Model Context Protocol client: exposes MCP server tools as mcp.<server>.<tool> agent tools.")]

namespace PiSharp.Mcp;

/// <summary>
/// Entry point for the MCP client plugin. Resolves the auth store through the C1 seam (falling
/// back to its own <see cref="FileOAuthStorage"/> at the agent auth path until Spine-1 exposes the
/// runtime instance on the binding), starts the host (settings subscription, <c>/mcp</c> command,
/// auto-connect reconcile), and stops it on disposal.
/// </summary>
public sealed class McpExtension : IExtension, IAsyncDisposable
{
    private McpClientHost? _host;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        // C1 seam: use the runtime's shared auth storage when Spine-1 exposes it on
        // ExtensionRuntimeBinding; until then open the same auth.json path ourselves.
        var authStorage = McpRuntimeAuthStorage.TryResolve()
            ?? new FileOAuthStorage(PiAgentPaths.FromCwd(api.Cwd).AuthPath);
        var host = new McpClientHost(api, McpTransportContext.Create(authStorage));
        await host.StartAsync(cancellationToken);
        _host = host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        await _host.StopAsync(CancellationToken.None);
        _host = null;
    }
}
