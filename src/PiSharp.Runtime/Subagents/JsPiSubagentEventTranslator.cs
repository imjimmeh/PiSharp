using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Runtime.Subagents;

/// <summary>
/// Translates PiSharp <see cref="AgentEvent"/> values into JavaScript Pi-compatible
/// JSON event shapes for subagent communication.
/// </summary>
public static class JsPiSubagentEventTranslator
{
    /// <summary>
    /// Translates a single <see cref="AgentEvent"/> into one or more JS Pi event objects.
    /// </summary>
    public static IEnumerable<object> Translate(AgentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt switch
        {
            AgentEvent.AgentStart => [new JsPiAgentStart("agent_start")],
            AgentEvent.AgentEnd e => [new JsPiAgentEnd("agent_end", e.Messages)],
            AgentEvent.TurnStart => [new JsPiTurnStart("turn_start")],
            AgentEvent.TurnEnd e => [new JsPiTurnEnd("turn_end", e.Message, e.ToolResults)],
            AgentEvent.MessageStart e => [new JsPiMessageStart("message_start", e.Message)],
            AgentEvent.MessageUpdate e => [new JsPiMessageUpdate("message_update", e.Message, e.AssistantMessageEvent)],
            AgentEvent.MessageEnd e => [new JsPiMessageEnd("message_end", e.Message)],
            AgentEvent.ToolExecutionStart e => [new JsPiToolExecutionStart("tool_execution_start", e.ToolCallId, e.ToolName, e.Arguments)],
            AgentEvent.ToolExecutionUpdate e => [new JsPiToolExecutionUpdate("tool_execution_update", e.ToolCallId, e.ToolName, e.Arguments, e.PartialResult)],
            AgentEvent.ToolExecutionEnd e => [new JsPiToolExecutionEnd("tool_execution_end", e.ToolCallId, e.ToolName, e.Result, e.IsError)],
            _ => throw new NotSupportedException($"Unsupported AgentEvent type: {evt.GetType().Name}")
        };
    }

    /// <summary>
    /// Translates a completed <see cref="AssistantMessage"/> into a JS Pi message_end event.
    /// </summary>
    public static IEnumerable<object> MessageEnd(AssistantMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return [new JsPiAssistantMessageEnd("message_end", message)];
    }

    internal record JsPiAgentStart(string Type);
    internal record JsPiAgentEnd(string Type, IReadOnlyList<AgentMessage> Messages);
    internal record JsPiTurnStart(string Type);
    internal record JsPiTurnEnd(string Type, AgentMessage Message, IReadOnlyList<ToolResultMessage> ToolResults);
    internal record JsPiMessageStart(string Type, AgentMessage Message);
    internal record JsPiMessageUpdate(string Type, AgentMessage Message, AssistantMessageEvent AssistantMessageEvent);
    internal record JsPiMessageEnd(string Type, AgentMessage Message);
    internal record JsPiToolExecutionStart(string Type, string ToolCallId, string ToolName, [property: JsonPropertyName("args")] JsonElement Arguments);
    internal record JsPiToolExecutionUpdate(string Type, string ToolCallId, string ToolName, [property: JsonPropertyName("args")] JsonElement Arguments, object PartialResult);
    internal record JsPiToolExecutionEnd(string Type, string ToolCallId, string ToolName, object Result, bool IsError);
    internal record JsPiAssistantMessageEnd(string Type, AssistantMessage Message);
}
