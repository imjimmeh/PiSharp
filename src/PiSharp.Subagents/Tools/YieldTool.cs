using PiSharp.Abstractions.Messages;

using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;

using PiSharp.Subagents.Validation;
using PiSharp.Tools;

namespace PiSharp.Subagents.Tools;

/// <summary>
/// The <c>yield</c> result tool, injected into every subagent harness (even when the agent's
/// <c>tools</c> list is restricted). Validates the submitted object against the effective output
/// schema and terminates the child turn, storing the validated JSON on the session handle.
/// </summary>
public sealed class YieldTool : IAgentTool
{
    private readonly JsonElement? _effectiveSchema;

    public YieldTool(JsonElement? effectiveSchema = null)
    {
        _effectiveSchema = effectiveSchema;
    }

    public string Name => "yield";
    public string Label => "yield";
    public string Description => "Returns a structured JSON result for this subagent and ends the turn. "
        + "The object MUST conform to the declared output schema.";

    public JsonElement ParametersSchema => ToolSchemas.FromType<YieldToolInput>();
    public ToolExecutionMode? ExecutionMode => ToolExecutionMode.Sequential;

    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web);

    public JsonElement PrepareArguments(JsonElement args)
        => args;

    public Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        var input = parameters.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<YieldToolInput>(parameters.GetRawText(), DeserializeOptions)
            : null;
        if (input is null || input.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return Task.FromResult(new AgentToolResult<object?>(
                [new TextContent("yield requires a JSON object argument with a 'data' property.")],
                null,
                Terminate: false));

        if (!AgentSchemaValidator.Validate(_effectiveSchema, input.Data, out var errors))
        {
            return Task.FromResult(new AgentToolResult<object?>(
                [new TextContent("yield rejected — output does not conform to the declared schema: " + string.Join("; ", errors))],
                null,
                Terminate: false));
        }

        return Task.FromResult(new AgentToolResult<object?>(
            [new TextContent(input.Data.GetRawText())],
            input.Data.Clone(),
            Terminate: true));
    }
}
