using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Agent.Core.Events;

/// <summary>
/// Closed discriminated union of agent lifecycle events.
/// Matches TypeScript AgentEvent (10 variants).
/// </summary>
public abstract record AgentEvent
{
    public sealed record AgentStart : AgentEvent;

    public sealed record AgentEnd(IReadOnlyList<AgentMessage> Messages) : AgentEvent;

    public sealed record TurnStart : AgentEvent;

    public sealed record TurnEnd(
        AgentMessage Message,
        IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;

    public sealed record MessageStart(AgentMessage Message) : AgentEvent;

    public sealed record MessageUpdate(
        AgentMessage Message,
        AssistantMessageEvent AssistantMessageEvent) : AgentEvent;

    public sealed record MessageEnd(AgentMessage Message) : AgentEvent;

    public sealed record ToolExecutionStart(
        string ToolCallId,
        string ToolName,
        JsonElement Arguments) : AgentEvent;

    public sealed record ToolExecutionUpdate(
        string ToolCallId,
        string ToolName,
        JsonElement Arguments,
        object PartialResult) : AgentEvent;

    public sealed record ToolExecutionEnd(
        string ToolCallId,
        string ToolName,
        object Result,
        bool IsError) : AgentEvent;
}
