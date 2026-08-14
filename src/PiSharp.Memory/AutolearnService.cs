using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;

namespace PiSharp.Memory;

/// <summary>
/// Auto-learn (GAP-13) capture pipeline, off by default. Subscribes to
/// <c>agent_end</c> (counts the turn's tool calls) and <c>settled</c> (fires once
/// when the harness goes idle with <c>NextTurnCount == 0</c> and the threshold is
/// met), then sends a capture <c>NextTurn</c> message nudging the model to call
/// <c>learn</c> for reusable lessons. With <c>autoContinue</c> the prompt is
/// directive; otherwise it is a decline-able nudge.
/// </summary>
public sealed class AutolearnService
{
    private readonly IExtensionApi _api;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _autoContinue;
    private readonly Func<int> _minToolCalls;

    private int _pendingToolCalls;
    private bool _agentEndSeen;

    public AutolearnService(
        IExtensionApi api,
        Func<bool> enabled,
        Func<bool> autoContinue,
        Func<int> minToolCalls)
    {
        _api = api;
        _enabled = enabled;
        _autoContinue = autoContinue;
        _minToolCalls = minToolCalls;
    }

    /// <summary>agent_end hook: records the ended turn's tool-call count when auto-learn is enabled.</summary>
    public Task OnAgentEndAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (!_enabled()) return Task.CompletedTask;
        _pendingToolCalls = CountToolCalls(evt.Payload);
        _agentEndSeen = true;
        return Task.CompletedTask;
    }

    /// <summary>settled hook: fires the capture exactly once per run when idle and the threshold is met.</summary>
    public async Task OnSettledAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (!_enabled() || !_agentEndSeen) return;
        _agentEndSeen = false;

        if (NextTurnCount(evt.Payload) is { } nextTurnCount && nextTurnCount > 0) return;
        var toolCalls = _pendingToolCalls;
        _pendingToolCalls = 0;
        if (toolCalls < _minToolCalls()) return;

        await RunCaptureAsync(toolCalls, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RunCaptureAsync(int toolCalls, CancellationToken cancellationToken)
    {
        var autoContinue = _autoContinue();
        await _api.EmitClientEventAsync(
            MemoryEventNames.AutolearnCaptureStart,
            new { toolCalls, autoContinue },
            cancellationToken).ConfigureAwait(false);

        var message = autoContinue
            ? "Memory capture (auto-learn): review the finished turn for reusable lessons. Call `learn` for each lesson worth keeping, or reply \"no lessons\" if nothing is reusable."
            : "Optional memory capture: if the finished turn produced reusable lessons, consider calling `learn` to store them. If nothing is worth keeping, reply \"no lessons\".";

        await _api.SendMessageAsync(
            AgentMessages.User(message),
            ExtensionMessageDelivery.NextTurn,
            triggerTurn: true,
            cancellationToken).ConfigureAwait(false);

        await _api.EmitClientEventAsync(
            MemoryEventNames.AutolearnCaptureEnd,
            new { lessonsStored = 0, skillsCreated = 0, declined = false },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts distinct tool calls in the ended turn's message list (agent_end payload).</summary>
    internal static int CountToolCalls(object? payload)
        => payload switch
        {
            AgentEvent.AgentEnd end => CountToolCalls(end.Messages),
            JsonElement element => CountToolCallsFromJson(element),
            _ => 0
        };

    private static int CountToolCalls(IReadOnlyList<AgentMessage> messages)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is ToolResultMessage { ToolUseId: var resultId }) ids.Add(resultId);
            foreach (var content in Contents(message))
            {
                if (content is ToolCallContent { Id: var callId }) ids.Add(callId);
            }
        }
        return ids.Count;
    }

    private static IEnumerable<MessageContent> Contents(AgentMessage message) => message switch
    {
        UserMessage user => user.Content,
        AssistantMessage assistant => assistant.Content,
        ToolResultMessage result => result.Content,
        _ => []
    };

    /// <summary>Counts tool calls from a JSON-serialized agent_end payload ({ "messages": [...] }).</summary>
    internal static int CountToolCallsFromJson(JsonElement element)
    {
        var messages = element.ValueKind switch
        {
            JsonValueKind.Array => element,
            _ when element.TryGetProperty("messages", out var messagesProperty) => messagesProperty,
            _ => default
        };
        if (messages.ValueKind != JsonValueKind.Array) return 0;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.EnumerateArray())
        {
            var role = ReadString(message, "role");
            if (role == "toolResult" && ReadString(message, "toolUseId") is { } resultId) ids.Add(resultId);
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    var itemType = ReadString(item, "type");
                    if (itemType is "toolCall" or "tool_call" && ReadString(item, "id") is { } callId) ids.Add(callId);
                }
            }
        }
        return ids.Count;
    }

    /// <summary>Reads the settled payload's NextTurnCount; null when the payload carries no turn count.</summary>
    internal static int? NextTurnCount(object? payload)
        => payload switch
        {
            AgentHarnessOwnEvent.Settled settled => settled.NextTurnCount,
            JsonElement element when element.TryGetProperty("nextTurnCount", out var property) && property.ValueKind == JsonValueKind.Number => property.GetInt32(),
            _ => null
        };

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
