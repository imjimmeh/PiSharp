using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;

namespace PiSharp.Agent.Core.Tools;

public delegate void AgentToolUpdateCallback<TDetails>(AgentToolResult<TDetails> partialResult);

public interface IAgentTool
{
    string Name { get; }

    string Label { get; }

    string Description { get; }

    string? PromptSnippet => null;

    IReadOnlyList<string> PromptGuidelines => [];

    JsonElement ParametersSchema { get; }

    ToolExecutionMode? ExecutionMode { get; }

    JsonElement PrepareArguments(JsonElement args);

    Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null);
}

public interface IAgentTool<TParameters, TDetails> : IAgentTool
{
    new TParameters PrepareArguments(JsonElement args);

    Task<AgentToolResult<TDetails>> ExecuteAsync(
        string toolCallId,
        TParameters parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<TDetails>? onUpdate = null);
}

public interface IAgentToolRenderer
{
    bool HasRenderCall { get; }

    bool HasRenderResult { get; }

    Task<ToolRenderResult?> RenderCallAsync(
        ToolRenderRequest request,
        CancellationToken cancellationToken = default);

    Task<ToolRenderResult?> RenderResultAsync(
        ToolRenderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ToolRenderRequest(
    string ToolCallId,
    string ToolName,
    JsonElement? Arguments,
    AgentToolResult<object?>? Result,
    bool IsPartial,
    bool IsError,
    bool Expanded,
    int Width);

public sealed record ToolRenderResult(IReadOnlyList<string> Lines);

public sealed record AgentToolResult<TDetails>(
    IReadOnlyList<MessageContent> Content,
    TDetails Details,
    bool Terminate = false);

public sealed record BeforeToolCallResult(
    bool Block = false,
    string? Reason = null);

public sealed record AfterToolCallResult(
    IReadOnlyList<MessageContent>? Content = null,
    object? Details = null,
    bool? IsError = null,
    bool? Terminate = null);
