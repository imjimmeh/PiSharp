using ModelContextProtocol.Client;

namespace PiSharp.Mcp.Transports.Stdio;

/// <summary>
/// Creates <see cref="StdioClientTransport"/> instances for stdio MCP servers. Extra environment
/// variables from the config are merged over the inherited process environment.
/// </summary>
public sealed class StdioTransportFactory : IMcpTransportFactory
{
    public string Kind => "stdio";

    public bool CanCreate(McpServerConfig config)
        => config.Transport == McpTransportKind.Stdio;

    public ValueTask<IClientTransport> CreateAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken)
    {
        var options = new StdioClientTransportOptions
        {
            Command = config.Command ?? throw new InvalidOperationException("A stdio MCP server requires a command."),
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
