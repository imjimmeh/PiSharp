using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Acp;

/// <summary>
/// Maps <see cref="AgentHarnessEvent"/> values onto ACP <c>session/update</c> payloads (plan §5.5).
/// One harness event yields zero-or-more update objects (the value of the <c>update</c> field);
/// the server wraps each in <c>{ sessionId, update }</c> and emits a <c>session/update</c> notification.
/// Message ids are assigned at <c>MessageStart</c> and keyed to the in-flight message by reference
/// identity (plan §3.7).
/// </summary>
public sealed class AcpEventTranslator
{
    private readonly string _sessionId;
    private readonly Func<AgentHarnessOwnEvent.ToolCall, (string Title, string Kind)> _toolMeta;
    private int _nextMessageId;
    private string? _activeMessageId;

    public AcpEventTranslator(string sessionId, Func<AgentHarnessOwnEvent.ToolCall, (string Title, string Kind)>? toolMeta = null)
    {
        _sessionId = sessionId;
        _toolMeta = toolMeta ?? (call => (call.ToolName, MapToolKind(call.ToolName)));
    }

    public string SessionId => _sessionId;

    public IEnumerable<object> Translate(AgentHarnessEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt switch
        {
            AgentHarnessEvent.Core { Event: AgentEvent.MessageStart e } when e.Message is AssistantMessage => MessageStart((AssistantMessage)e.Message),
            AgentHarnessEvent.Core { Event: AgentEvent.MessageUpdate e } => MessageUpdate(e),
            AgentHarnessEvent.Core { Event: AgentEvent.MessageEnd } => [],
            AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionStart e } => [ToolCallUpdate(e.ToolCallId, "in_progress", null)],
            AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionEnd e } => [ToolCallUpdate(e.ToolCallId, e.IsError ? "failed" : "completed", ContentItems(e.Result))],
            AgentHarnessEvent.Core { Event: AgentEvent.TurnEnd e } => TurnEnd(e),
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ToolCall e } => ToolCall(e),
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionInfoChanged e } => SessionInfo(e.Name),
            _ => []
        };
    }

    /// <summary>
    /// Replay translation for <c>session/load</c> (plan §5.5). Translates a completed context
    /// (from <c>Session.BuildContextAsync</c>) into user/agent message chunks and synthesized
    /// completed tool-call updates.
    /// </summary>
    public IEnumerable<object> TranslateReplay(IEnumerable<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<object>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    result.Add(UserChunk(AssignMessageId(), string.Concat(user.Content.OfType<TextContent>().Select(c => c.Text))));
                    break;
                case AssistantMessage assistant:
                {
                    var messageId = AssignMessageId();
                    var text = string.Concat(assistant.Content.OfType<TextContent>().Select(c => c.Text));
                    var thinking = string.Concat(assistant.Content.OfType<ThinkingContent>().Select(c => c.Thinking));
                    if (text.Length > 0) result.Add(MessageChunk(messageId, text));
                    if (thinking.Length > 0) result.Add(ThoughtChunk(messageId, thinking));
                    break;
                }
                case ToolResultMessage toolResult:
                    result.Add(new { sessionUpdate = "tool_call_update", toolCallId = toolResult.ToolUseId, status = "completed", content = ContentItems(toolResult.Content) });
                    break;
            }
        }
        return result;
    }

    private IEnumerable<object> MessageStart(AssistantMessage message)
    {
        var messageId = AssignMessageId();
        _activeMessageId = messageId;
        var text = string.Concat(message.Content.OfType<TextContent>().Select(c => c.Text));
        var thinking = string.Concat(message.Content.OfType<ThinkingContent>().Select(c => c.Thinking));
        if (text.Length > 0) yield return MessageChunk(messageId, text);
        if (thinking.Length > 0) yield return ThoughtChunk(messageId, thinking);
    }

    private IEnumerable<object> MessageUpdate(AgentEvent.MessageUpdate update) => update.AssistantMessageEvent switch
    {
        AssistantMessageEvent.TextDelta delta => [MessageChunk(ActiveMessageId(), delta.Delta)],
        AssistantMessageEvent.ThinkingDelta delta => [ThoughtChunk(ActiveMessageId(), delta.Delta)],
        _ => []
    };

    private IEnumerable<object> TurnEnd(AgentEvent.TurnEnd turnEnd)
    {
        var usage = (turnEnd.Message as AssistantMessage)?.Usage;
        return usage is null
            ? [new { sessionUpdate = "usage_update" }]
            : [new { sessionUpdate = "usage_update", used = usage.Input + usage.Output, size = (object?)null, cost = usage.Cost }];
    }

    private IEnumerable<object> ToolCall(AgentHarnessOwnEvent.ToolCall call)
    {
        var (title, kind) = _toolMeta(call);
        yield return new { sessionUpdate = "tool_call", toolCallId = call.ToolCallId, title, kind, status = "pending", rawInput = call.Arguments };
    }

    private static IEnumerable<object> SessionInfo(string? name)
        => [new { sessionUpdate = "session_info_update", title = name ?? string.Empty }];

    private static object ToolCallUpdate(string toolCallId, string status, object? content)
        => new { sessionUpdate = "tool_call_update", toolCallId, status, content };

    private static object MessageChunk(string messageId, string text)
        => new { sessionUpdate = "agent_message_chunk", messageId, content = new { type = "text", text } };

    private static object ThoughtChunk(string messageId, string text)
        => new { sessionUpdate = "agent_thought_chunk", messageId, content = new { type = "text", text } };

    private static object UserChunk(string messageId, string text)
        => new { sessionUpdate = "user_message_chunk", messageId, content = new { type = "text", text } };

    private string ActiveMessageId() => _activeMessageId ??= AssignMessageId();

    private string AssignMessageId() => $"msg_{_nextMessageId++}";

    private static object[] ContentItems(object result)
        => [new { type = "content", content = new { type = "text", text = result?.ToString() ?? string.Empty } }];

    private static object[] ContentItems(IReadOnlyList<MessageContent> content)
        => content.OfType<TextContent>().Select(t => (object)new { type = "content", content = new { type = "text", text = t.Text } }).ToArray();

    /// <summary>Maps a tool name to its ACP <c>ToolKind</c> (plan §5.6).</summary>
    public static string MapToolKind(string toolName) => toolName switch
    {
        "read" => "read",
        "grep" or "find" or "ls" => "search",
        "edit" or "write" => "edit",
        "bash" => "execute",
        _ => "other"
    };
}
