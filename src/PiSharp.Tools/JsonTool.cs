using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Tools;

public abstract class JsonTool<TParameters, TDetails>(JsonElement parametersSchema) : IAgentTool<TParameters, TDetails>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public abstract string Name { get; }
    public virtual string Label => Name;
    public abstract string Description { get; }
    public virtual string? PromptSnippet => null;
    public virtual IReadOnlyList<string> PromptGuidelines => [];
    public JsonElement ParametersSchema { get; } = parametersSchema.Clone();
    public virtual ToolExecutionMode? ExecutionMode => null;

    JsonElement IAgentTool.PrepareArguments(JsonElement args) => PrepareArgumentsElement(args);

    public virtual TParameters PrepareArguments(JsonElement args)
    {
        var prepared = PrepareArgumentsElement(args);
        return JsonSerializer.Deserialize<TParameters>(prepared.GetRawText(), SerializerOptions)
            ?? throw new InvalidOperationException($"Could not deserialize arguments for tool '{Name}'.");
    }

    public abstract Task<AgentToolResult<TDetails>> ExecuteAsync(
        string toolCallId,
        TParameters parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<TDetails>? onUpdate = null);

    async Task<AgentToolResult<object?>> IAgentTool.ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken,
        AgentToolUpdateCallback<object?>? onUpdate)
    {
        AgentToolUpdateCallback<TDetails>? typedUpdate = onUpdate is null
            ? null
            : partial => onUpdate(new AgentToolResult<object?>(partial.Content, partial.Details, partial.Terminate));
        var result = await ExecuteAsync(toolCallId, PrepareArguments(parameters), cancellationToken, typedUpdate).ConfigureAwait(false);
        return new AgentToolResult<object?>(result.Content, result.Details, result.Terminate);
    }

    protected virtual JsonElement PrepareArgumentsElement(JsonElement args) => args.Clone();

    protected static AgentToolResult<T> TextResult<T>(string text, T details, bool terminate = false)
        => new([new TextContent(text)], details, terminate);
}
