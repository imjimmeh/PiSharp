using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge;

public sealed class TsBridgeTool(TsToolDefinition definition, TsExtensionHost host, Func<string, CancellationToken, Task>? ensureReadyAsync = null) : IAgentTool, IAgentToolRenderer
{
    public string Name => definition.Name;
    public string Label => definition.Label;
    public string Description => definition.Description;
    public string? PromptSnippet => definition.PromptSnippet;
    public IReadOnlyList<string> PromptGuidelines => definition.PromptGuidelines ?? [];
    public JsonElement ParametersSchema => definition.Parameters;
    public ToolExecutionMode? ExecutionMode => definition.ExecutionMode;
    public bool HasRenderCall => definition.HasRenderCall;
    public bool HasRenderResult => definition.HasRenderResult;
    public JsonElement PrepareArguments(JsonElement args) => args;

    public async Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        if (ensureReadyAsync is not null) await ensureReadyAsync(definition.ExtensionId, cancellationToken);
        var result = await host.InvokeToolAsync(new TsToolCallRequest(definition.ExtensionId, Name, parameters, toolCallId), cancellationToken);
        return new AgentToolResult<object?>(result.Content, result.Details, result.Terminate);
    }

    public async Task<ToolRenderResult?> RenderCallAsync(ToolRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (!HasRenderCall) return null;
        if (ensureReadyAsync is not null) await ensureReadyAsync(definition.ExtensionId, cancellationToken);
        var rendered = await host.RenderToolCallAsync(ToTsRequest(request, null), cancellationToken);
        return rendered is null ? null : new ToolRenderResult(rendered.Lines);
    }

    public async Task<ToolRenderResult?> RenderResultAsync(ToolRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (!HasRenderResult) return null;
        if (ensureReadyAsync is not null) await ensureReadyAsync(definition.ExtensionId, cancellationToken);
        var rendered = await host.RenderToolResultAsync(ToTsRequest(request, ToTsResult(request.Result)), cancellationToken);
        return rendered is null ? null : new ToolRenderResult(rendered.Lines);
    }

    private TsToolRenderRequest ToTsRequest(ToolRenderRequest request, TsToolCallResult? result)
        => new(definition.ExtensionId, Name, request.ToolCallId, request.Arguments, result, request.IsPartial, request.IsError, request.Expanded, request.Width);

    private static TsToolCallResult? ToTsResult(AgentToolResult<object?>? result)
        => result is null ? null : new TsToolCallResult(result.Content, result.Details, result.Terminate);
}
