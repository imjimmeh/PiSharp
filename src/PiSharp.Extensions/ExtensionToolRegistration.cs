using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Extensions;

public delegate Task<AgentToolResult<object?>> ExtensionToolExecuteAsync(
    string toolCallId,
    JsonElement parameters,
    CancellationToken cancellationToken,
    AgentToolUpdateCallback<object?>? onUpdate);

public delegate JsonElement ExtensionToolPrepareArguments(JsonElement args);

public sealed record ExtensionToolRegistration(
    string Name,
    string Label,
    string Description,
    JsonElement ParametersSchema,
    ExtensionToolExecuteAsync ExecuteAsync,
    ToolExecutionMode? ExecutionMode = null,
    string? PromptSnippet = null,
    IReadOnlyList<string>? PromptGuidelines = null,
    ExtensionToolPrepareArguments? PrepareArguments = null,
    string? RenderShell = null,
    string? RendererName = null,
    ExtensionOverridePolicy Override = ExtensionOverridePolicy.Reject)
{
    public IAgentTool ToAgentTool() => new ExtensionRegisteredTool(this);
}

public sealed class ExtensionRegisteredTool(ExtensionToolRegistration registration) : IAgentTool
{
    public string Name => registration.Name;
    public string Label => registration.Label;
    public string Description => registration.Description;
    public string? PromptSnippet => registration.PromptSnippet;
    public IReadOnlyList<string> PromptGuidelines => registration.PromptGuidelines ?? [];
    public JsonElement ParametersSchema => registration.ParametersSchema;
    public ToolExecutionMode? ExecutionMode => registration.ExecutionMode;
    public JsonElement PrepareArguments(JsonElement args) => registration.PrepareArguments?.Invoke(args) ?? args;

    public Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null)
        => registration.ExecuteAsync(toolCallId, parameters, cancellationToken, onUpdate);
}
