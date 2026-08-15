using ModelContextProtocol.Client;
using PiSharp.Extensions;

namespace PiSharp.Mcp.Transports.Stdio;

/// <summary>
/// Creates <see cref="StdioClientTransport"/> instances for stdio MCP servers. Extra environment
/// variables from the config are merged over the inherited process environment. The command is
/// gated by the transport context's spawn gate (falling back to <see cref="CapabilityGates.McpSpawn"/>):
/// a denial rejects the spawn, surfacing as the server's per-server error state.
/// </summary>
public sealed class StdioTransportFactory : IMcpTransportFactory
{
    public string Kind => "stdio";

    public bool CanCreate(McpServerConfig config)
        => config.Transport == McpTransportKind.Stdio;

    public ValueTask<IClientTransport> CreateAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken)
    {
        var command = config.Command ?? throw new InvalidOperationException("A stdio MCP server requires a command.");
        var gate = context.SpawnGate ?? CapabilityGates.EvaluateMcpSpawn;
        var denial = gate(new SpawnRequest("mcp", command, config.Args, SourceId: config.SourceId, Name: config.Name));
        if (denial is not null)
        {
            throw new InvalidOperationException($"MCP server '{config.Name}' spawn blocked by the permission gate: {denial}");
        }

        var options = new StdioClientTransportOptions
        {
            Command = command,
            Arguments = (config.Args ?? []).ToList(),
            WorkingDirectory = config.Cwd,
            InheritEnvironmentVariables = true,
            Name = config.Name
        };

        if (config.Env is { Count: > 0 })
        {
            options.EnvironmentVariables = new Dictionary<string, string?>(config.Env, StringComparer.OrdinalIgnoreCase);
        }

        options.StandardErrorLines = line => context.Log($"MCP server '{config.Name}' stderr: {line}");
        return ValueTask.FromResult<IClientTransport>(new StdioClientTransport(options, loggerFactory: null));
    }
}
