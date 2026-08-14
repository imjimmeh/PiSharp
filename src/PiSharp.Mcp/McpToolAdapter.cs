using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Tools.Shared;

namespace PiSharp.Mcp;

/// <summary>
/// Maps an SDK <see cref="Tool"/> into a model-facing <see cref="ExtensionToolRegistration"/>
/// named <c>&lt;toolPrefix&gt;.&lt;server&gt;.&lt;tool&gt;</c>. Parameters are forwarded verbatim to
/// <c>CallToolAsync</c>; results are formatted and bounded by <see cref="McpResultFormatter"/>.
/// </summary>
public sealed class McpToolAdapter
{
    /// <summary>MCP spec tool-name constraint; violating servers are skipped with a warning.</summary>
    private static readonly Regex ToolNamePattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    private readonly string _serverName;
    private readonly string _toolPrefix;
    private readonly TruncationOptions? _truncation;
    private readonly Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task<CallToolResult>> _callAsync;

    public McpToolAdapter(
        string serverName,
        string toolPrefix,
        TruncationOptions? truncation,
        Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task<CallToolResult>> callAsync)
    {
        _serverName = serverName;
        _toolPrefix = toolPrefix;
        _truncation = truncation;
        _callAsync = callAsync;
    }

    public static bool IsValidToolName(string name) => ToolNamePattern.IsMatch(name);

    public ExtensionToolRegistration ToRegistration(McpClientTool clientTool)
    {
        var tool = clientTool.ProtocolTool;
        var toolName = tool.Name ?? string.Empty;
        var label = $"{_serverName}.{toolName}";
        var callAsync = _callAsync;
        var serverName = _serverName;
        var truncation = _truncation;
        return new ExtensionToolRegistration(
            Name: $"{_toolPrefix}.{_serverName}.{toolName}",
            Label: label,
            Description: string.IsNullOrWhiteSpace(tool.Description)
                ? $"MCP tool '{label}'."
                : tool.Description,
            ParametersSchema: tool.InputSchema,
            ExecuteAsync: (toolCallId, parameters, cancellationToken, onUpdate)
                => ExecuteAsync(callAsync, serverName, toolName, truncation, parameters, cancellationToken),
            PromptGuidelines: [],
            PrepareArguments: args => args);
    }

    public static async Task<AgentToolResult<object?>> ExecuteAsync(
        Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task<CallToolResult>> callAsync,
        string serverName,
        string toolName,
        TruncationOptions? truncation,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var args = ToArguments(parameters);
        var result = await callAsync(toolName, args, cancellationToken);
        var text = McpResultFormatter.Format(result, serverName, truncation);
        return new AgentToolResult<object?>(
            [new TextContent(text)],
            null);
    }

    /// <summary>Converts a JSON object element into the argument dictionary the SDK expects.</summary>
    private static IReadOnlyDictionary<string, object?>? ToArguments(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
        {
            arguments[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.Clone();
        }
        return arguments;
    }
}
