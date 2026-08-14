using PiSharp.Abstractions.Messages;

using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;

using PiSharp.Extensions;
using PiSharp.Subagents.Spawning;
using PiSharp.Tools;

namespace PiSharp.Subagents.Tools;

/// <summary>
/// The model-callable <c>task</c> spawn tool: resolves the agent definition, applies spawn policy
/// (disabled / self-recursion / depth / <c>spawns</c> allowlist), drives the child session via the
/// coordinator, and returns the structured result as its tool result. Blocked spawns surface as
/// readable error tool results.
/// </summary>
public sealed class TaskTool : IAgentTool
{
    private readonly SubagentSpawnCoordinator _coordinator;

    public TaskTool(SubagentSpawnCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public string Name => "task";
    public string Label => "task";
    public string Description => "Spawns a subagent from a named agent definition and returns its structured result. "
        + "The subagent runs with its own system prompt, tools, and output schema.";

    public JsonElement ParametersSchema => ToolSchemas.FromType<TaskToolInput>();
    public ToolExecutionMode? ExecutionMode => ToolExecutionMode.Sequential;

    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web);

    public JsonElement PrepareArguments(JsonElement args) => args;

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        var input = parameters.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<TaskToolInput>(parameters.GetRawText(), DeserializeOptions)
            : null;
        if (input is null || input.Agent is null || input.Task is null)
            return ErrorResult("task requires a JSON object argument with 'agent' and 'task' properties.");

        var outcome = await _coordinator.SpawnAsync(input, toolCallId, cancellationToken);
        if (!outcome.Success)
            return ErrorResult(outcome.BlockReason is not null
                ? $"task blocked: cannot spawn agent '{input.Agent}' ({outcome.BlockReason})."
                : $"task failed: {outcome.Error}.");

        var text = outcome.StructuredResult is { } structured
            ? structured.GetRawText()
            : "completed.";
        return new AgentToolResult<object?>([new TextContent(text)], outcome.StructuredResult is { } result ? result : new { }, Terminate: false);
    }

    /// <summary>Wraps this tool as an <see cref="ExtensionToolRegistration"/> for registration on the
    /// parent harness via <c>IExtensionApi.RegisterTool</c>.</summary>
    public ExtensionToolRegistration ToRegistration()
        => new(
            Name,
            Label,
            Description,
            ParametersSchema,
            (toolCallId, parameters, cancellationToken, onUpdate) =>
                ExecuteAsync(toolCallId, parameters, cancellationToken, onUpdate),
            ExecutionMode: ExecutionMode,
            PrepareArguments: PrepareArguments);

    private static AgentToolResult<object?> ErrorResult(string message)
        => new([new TextContent(message)], new { }, Terminate: false);
}
