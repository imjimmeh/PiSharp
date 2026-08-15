using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;
using PiSharp.Server.Contracts;

namespace PiSharp.Client;

/// <summary>
/// Client-side stand-in for a daemon-hosted extension tool resolved via <c>resolve_tool</c>.
/// Execution stays daemon-side; the client only renders call/result lines by round-tripping
/// <c>render_tool_call</c>/<c>render_tool_result</c> to the server. Tools without a renderer
/// (both wire capability flags false) are metadata-only and keep the TUI's text-row fallback.
/// </summary>
public sealed class RemoteRegisteredTool(ExtensionToolWire wire, RemoteTuiBackend backend) : IAgentTool, IAgentToolRenderer
{
    public string Name => wire.Name;
    public string Label => wire.Label;
    public string Description => wire.Description;
    public string? PromptSnippet => wire.PromptSnippet;
    public IReadOnlyList<string> PromptGuidelines => wire.PromptGuidelines ?? [];
    public JsonElement ParametersSchema => wire.ParametersSchema;
    public ToolExecutionMode? ExecutionMode => wire.ExecutionMode;
    public bool HasRenderCall => wire.HasRenderCall;
    public bool HasRenderResult => wire.HasRenderResult;
    public JsonElement PrepareArguments(JsonElement args) => args;

    public Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null)
        => throw new NotSupportedException("Remote tools execute daemon-side; the client only renders them.");

    public Task<ToolRenderResult?> RenderCallAsync(ToolRenderRequest request, CancellationToken cancellationToken)
        => backend.RenderToolCallAsync(wire.Name, request, cancellationToken);

    public Task<ToolRenderResult?> RenderResultAsync(ToolRenderRequest request, CancellationToken cancellationToken)
        => backend.RenderToolResultAsync(wire.Name, request, cancellationToken);
}
