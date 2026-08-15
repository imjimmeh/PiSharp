using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Core.Events;

/// <summary>
/// Top-level union: either a core agent event or a harness-owned event.
/// </summary>
public abstract record AgentHarnessEvent
{
    public sealed record Core(AgentEvent Event) : AgentHarnessEvent;

    public sealed record Own(AgentHarnessOwnEvent Event) : AgentHarnessEvent;
}

/// <summary>
/// JavaScript-compatible flat session event shape used at the RPC boundary.
/// </summary>
public sealed class AgentSessionEvent
{
    private AgentSessionEvent(string type, object? data)
    {
        Type = type;
        Data = data;
    }

    public string Type { get; }
    public object? Data { get; }

    public static AgentSessionEvent FromCore(AgentEvent coreEvent)
        => coreEvent switch
        {
            AgentEvent.AgentStart => new AgentSessionEvent("agent_start", null),
            AgentEvent.AgentEnd e => new AgentSessionEvent("agent_end", new { messages = e.Messages }),
            AgentEvent.TurnStart => new AgentSessionEvent("turn_start", null),
            AgentEvent.TurnEnd e => new AgentSessionEvent("turn_end", new { message = e.Message, toolResults = e.ToolResults }),
            AgentEvent.MessageStart e => new AgentSessionEvent("message_start", new { message = e.Message }),
            AgentEvent.MessageUpdate e => new AgentSessionEvent("message_update", new { message = e.Message, assistantMessageEvent = e.AssistantMessageEvent }),
            AgentEvent.MessageEnd e => new AgentSessionEvent("message_end", new { message = e.Message }),
            AgentEvent.ToolExecutionStart e => new AgentSessionEvent("tool_execution_start", new { toolCallId = e.ToolCallId, toolName = e.ToolName, arguments = e.Arguments }),
            AgentEvent.ToolExecutionUpdate e => new AgentSessionEvent("tool_execution_update", new { toolCallId = e.ToolCallId, toolName = e.ToolName, arguments = e.Arguments, partialResult = e.PartialResult }),
            AgentEvent.ToolExecutionEnd e => new AgentSessionEvent("tool_execution_end", new { toolCallId = e.ToolCallId, toolName = e.ToolName, result = e.Result, isError = e.IsError }),
            AgentEvent.AutoRetryStart e => new AgentSessionEvent("auto_retry_start", new { attempt = e.Attempt, maxAttempts = e.MaxAttempts, delayMs = e.DelayMs, errorMessage = e.ErrorMessage }),
            AgentEvent.AutoRetryEnd e => new AgentSessionEvent("auto_retry_end", new { success = e.Success, attempt = e.Attempt, finalError = e.FinalError }),
            _ => throw new NotSupportedException($"Unknown core event: {coreEvent.GetType().Name}")
        };

    public static AgentSessionEvent FromOwn(AgentHarnessOwnEvent ownEvent)
        => ownEvent switch
        {
            AgentHarnessOwnEvent.SessionStart e => new AgentSessionEvent("session_start", new { reason = e.Reason }),
            AgentHarnessOwnEvent.Input e => new AgentSessionEvent("input", new { text = e.Text, images = e.Images, source = e.Source }),
            AgentHarnessOwnEvent.SessionBeforeSwitch e => new AgentSessionEvent("session_before_switch", new { reason = e.Reason, targetSessionFile = e.TargetSessionFile, currentSession = e.CurrentSession, targetSession = e.TargetSession }),
            AgentHarnessOwnEvent.SessionBeforeFork e => new AgentSessionEvent("session_before_fork", new { entryId = e.EntryId, position = e.Position, sourceSession = e.SourceSession, forkOptions = e.ForkOptions }),
            AgentHarnessOwnEvent.SessionShutdown e => new AgentSessionEvent("session_shutdown", new { reason = e.Reason, targetSessionFile = e.TargetSessionFile, session = e.Session }),
            AgentHarnessOwnEvent.QueueUpdate e => new AgentSessionEvent("queue_update", new { steering = e.Steer, followUp = e.FollowUp, nextTurn = e.NextTurn }),
            AgentHarnessOwnEvent.CompactionStart e => new AgentSessionEvent("compaction_start", new { reason = e.Reason }),
            AgentHarnessOwnEvent.CompactionEnd e => new AgentSessionEvent("compaction_end", new { reason = e.Reason, result = e.Result, aborted = e.Aborted, willRetry = e.WillRetry, errorMessage = e.ErrorMessage }),
            AgentHarnessOwnEvent.AutoRetryStart e => new AgentSessionEvent("auto_retry_start", new { attempt = e.Attempt, maxAttempts = e.MaxAttempts, delayMs = e.DelayMs, errorMessage = e.ErrorMessage }),
            AgentHarnessOwnEvent.AutoRetryEnd e => new AgentSessionEvent("auto_retry_end", new { success = e.Success, attempt = e.Attempt, finalError = e.FinalError }),
            AgentHarnessOwnEvent.SessionInfoChanged e => new AgentSessionEvent("session_info_changed", new { name = e.Name }),
            AgentHarnessOwnEvent.ThinkingLevelChanged e => new AgentSessionEvent("thinking_level_changed", new { level = ToJsonValue(e.Level) }),
            AgentHarnessOwnEvent.ModelSelect e => new AgentSessionEvent("model_select", new { model = e.Model, previousModel = e.PreviousModel, source = e.Source }),
            AgentHarnessOwnEvent.ThinkingLevelSelect e => new AgentSessionEvent("thinking_level_select", new { level = ToJsonValue(e.Level), previousLevel = ToJsonValue(e.PreviousLevel) }),
            AgentHarnessOwnEvent.BeforeAgentStart e => new AgentSessionEvent("before_agent_start", new { prompt = e.Prompt, images = e.Images, systemPrompt = e.SystemPrompt, resources = e.Resources }),
            AgentHarnessOwnEvent.BeforePromptRender e => new AgentSessionEvent("before_prompt_render", new { prompt = e.Prompt, images = e.Images, resources = e.Resources }),
            AgentHarnessOwnEvent.SessionBeforeCompact e => new AgentSessionEvent("session_before_compact", new { preparation = e.Preparation, entries = e.BranchEntries, customInstructions = e.CustomInstructions }),
            AgentHarnessOwnEvent.SessionCompact e => new AgentSessionEvent("session_compact", new { compaction = e.CompactionEntry, fromHook = e.FromHook }),
            AgentHarnessOwnEvent.BeforeProviderRequest e => new AgentSessionEvent("before_provider_request", new { model = e.Model, sessionId = e.SessionId, streamOptions = e.StreamOptions }),
            AgentHarnessOwnEvent.BeforeProviderPayload e => new AgentSessionEvent("before_provider_payload", new { model = e.Model, payload = e.Payload }),
            AgentHarnessOwnEvent.AfterProviderResponse e => new AgentSessionEvent("after_provider_response", new { status = e.Status, headers = e.Headers }),
            AgentHarnessOwnEvent.ToolCall e => new AgentSessionEvent("tool_call", new { toolCallId = e.ToolCallId, toolName = e.ToolName, input = e.Arguments }),
            AgentHarnessOwnEvent.ToolResult e => new AgentSessionEvent("tool_result", new { toolCallId = e.ToolCallId, toolName = e.ToolName, input = e.Arguments, content = e.Content, details = e.Details, isError = e.IsError }),
            AgentHarnessOwnEvent.SavePoint e => new AgentSessionEvent("save_point", new { hadPendingMutations = e.HadPendingMutations }),
            AgentHarnessOwnEvent.Settled e => new AgentSessionEvent("settled", new { nextTurnCount = e.NextTurnCount }),
            AgentHarnessOwnEvent.Abort e => new AgentSessionEvent("abort", new { clearedSteer = e.ClearedSteer, clearedFollowUp = e.ClearedFollowUp }),
            AgentHarnessOwnEvent.Context e => new AgentSessionEvent("context", new { messages = e.Messages }),
            AgentHarnessOwnEvent.SessionBeforeTree e => new AgentSessionEvent("session_before_tree", new { preparation = e.Preparation }),
            AgentHarnessOwnEvent.SessionTree e => new AgentSessionEvent("session_tree", new { newLeafId = e.NewLeafId, oldLeafId = e.OldLeafId, summaryEntry = e.SummaryEntry, fromHook = e.FromHook }),
            AgentHarnessOwnEvent.ResourcesUpdate e => new AgentSessionEvent("resources_update", new { resources = e.Resources, previousResources = e.PreviousResources }),
            AgentHarnessOwnEvent.AdvisorNote e => FromAdvisor(e.Event),
            AgentHarnessOwnEvent.SystemMessage e => new AgentSessionEvent("system_message", new { text = e.Text, isError = e.IsError }),
            AgentHarnessOwnEvent.CustomEvent e => new AgentSessionEvent(e.Name, e.Payload),
            _ => throw new NotSupportedException($"Unknown own event: {ownEvent.GetType().Name}")
        };

    private static readonly System.Text.RegularExpressions.Regex CustomEventNamePattern = new(
        "^[a-z0-9_]{1,64}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedEventNames = new(StringComparer.Ordinal)
    {
        "session_start", "input", "session_before_switch", "session_before_fork", "session_shutdown",
        "queue_update", "compaction_start", "compaction_end", "auto_retry_start", "auto_retry_end",
        "session_info_changed", "thinking_level_changed", "model_select", "thinking_level_select",
        "before_agent_start", "before_prompt_render", "session_before_compact", "session_compact",
        "before_provider_request", "before_provider_payload", "after_provider_response", "tool_call",
        "tool_result", "save_point", "settled", "abort", "context", "session_before_tree",
        "session_tree", "resources_update", "advisor_note"
    };

    /// <summary>
    /// Validates a <see cref="AgentHarnessOwnEvent.CustomEvent"/> name at publish time:
    /// snake_case <c>[a-z0-9_]{1,64}</c> and no collision with a core session event name.
    /// Throws <see cref="ArgumentException"/> when invalid.
    /// </summary>
    public static void ValidateCustomEventName(string name)
    {
        if (string.IsNullOrEmpty(name) || !CustomEventNamePattern.IsMatch(name))
            throw new ArgumentException($"Custom event name '{name}' is invalid: must match [a-z0-9_]{{1,64}}.", nameof(name));
        if (ReservedEventNames.Contains(name))
            throw new ArgumentException($"Custom event name '{name}' collides with a core session event name.", nameof(name));
    }

    /// <summary>
    /// Maps a custom event whose name is reserved for a dedicated session event onto that
    /// event's typed variant, so reserved names can ride the custom-event lane without
    /// colliding (e.g. <c>advisor_note</c> → <see cref="AgentHarnessOwnEvent.AdvisorNote"/>,
    /// flattened via <see cref="FromAdvisor(ExtensionAdvisorEvent)"/>). Returns <c>null</c>
    /// for non-reserved names, which proceed through normal custom-event validation. Throws
    /// <see cref="ArgumentException"/> when the name is reserved but has no dedicated mapping,
    /// or when its payload does not match the mapped variant's payload type.
    /// </summary>
    public static AgentHarnessOwnEvent? MapReservedCustomEvent(AgentHarnessOwnEvent.CustomEvent customEvent)
    {
        if (!ReservedEventNames.Contains(customEvent.Name)) return null;

        return customEvent.Name switch
        {
            "advisor_note" => customEvent.Payload is ExtensionAdvisorEvent advisorEvent
                ? new AgentHarnessOwnEvent.AdvisorNote(advisorEvent)
                : throw new ArgumentException(
                    $"Custom event 'advisor_note' requires an {nameof(ExtensionAdvisorEvent)} payload.", nameof(customEvent)),
            _ => throw new ArgumentException(
                $"Custom event name '{customEvent.Name}' collides with a core session event name.", nameof(customEvent))
        };
    }

    public static AgentSessionEvent FromAdvisor(ExtensionAdvisorEvent e)
        => new AgentSessionEvent("advisor_note", new { sessionId = e.SessionId, turnId = e.TurnId, kind = e.Note.Kind, text = e.Note.Text, toolName = e.Note.ToolName, model = e.Note.Model });
    /// <summary>
    /// Creates a flat session event with an arbitrary server-defined type (e.g. <c>ui_request</c>).
    /// The client transport already handles unknown type strings.
    /// </summary>
    public static AgentSessionEvent FromServer(string type, object? data)
        => new(type, data);

    private static string ToJsonValue(ThinkingLevel level) => level.ToString().ToLowerInvariant();
}

public static class AgentHarnessEventExtensions
{
    public static AgentSessionEvent ToFlat(this AgentHarnessEvent evt)
        => evt switch
        {
            AgentHarnessEvent.Core core => AgentSessionEvent.FromCore(core.Event),
            AgentHarnessEvent.Own own => AgentSessionEvent.FromOwn(own.Event),
            _ => throw new NotSupportedException($"Unknown harness event: {evt.GetType().Name}")
        };
}

/// <summary>
/// Harness-owned events beyond the core agent lifecycle.
/// Sealed variants matching the TypeScript AgentHarnessOwnEvent plus app-facing session events.
/// </summary>
public abstract record AgentHarnessOwnEvent
{
    public sealed record SessionStart(string Reason) : AgentHarnessOwnEvent;

    public sealed record Input(
        string Text,
        IReadOnlyList<ImageContent>? Images,
        string Source) : AgentHarnessOwnEvent;

    public sealed record SessionBeforeSwitch(
        string Reason,
        string? TargetSessionFile,
        object? CurrentSession,
        object? TargetSession,
        CancellationToken Signal) : AgentHarnessOwnEvent;

    public sealed record SessionBeforeFork(
        string EntryId,
        string Position,
        object? SourceSession,
        object? ForkOptions,
        CancellationToken Signal) : AgentHarnessOwnEvent;

    public sealed record SessionShutdown(
        string Reason,
        string? TargetSessionFile = null,
        object? Session = null) : AgentHarnessOwnEvent;

    public sealed record QueueUpdate(
        IReadOnlyList<AgentMessage> Steer,
        IReadOnlyList<AgentMessage> FollowUp,
        IReadOnlyList<AgentMessage> NextTurn) : AgentHarnessOwnEvent;

    public sealed record CompactionStart(string Reason) : AgentHarnessOwnEvent;

    public sealed record CompactionEnd(
        string Reason,
        object? Result,
        bool Aborted,
        bool WillRetry,
        string? ErrorMessage) : AgentHarnessOwnEvent;

    public sealed record AutoRetryStart(
        int Attempt,
        int MaxAttempts,
        long DelayMs,
        string ErrorMessage) : AgentHarnessOwnEvent;

    public sealed record AutoRetryEnd(
        bool Success,
        int Attempt,
        string? FinalError) : AgentHarnessOwnEvent;

    public sealed record SessionInfoChanged(string? Name) : AgentHarnessOwnEvent;

    public sealed record ThinkingLevelChanged(ThinkingLevel Level) : AgentHarnessOwnEvent;

    public sealed record SavePoint(bool HadPendingMutations) : AgentHarnessOwnEvent;

    public sealed record Abort(
        IReadOnlyList<AgentMessage> ClearedSteer,
        IReadOnlyList<AgentMessage> ClearedFollowUp) : AgentHarnessOwnEvent;

    public sealed record Settled(int NextTurnCount) : AgentHarnessOwnEvent;

    public sealed record BeforeAgentStart(
        string Prompt,
        IReadOnlyList<ImageContent>? Images,
        string SystemPrompt,
        object Resources) : AgentHarnessOwnEvent;

    public sealed record BeforePromptRender(
        string Prompt,
        IReadOnlyList<ImageContent>? Images,
        SystemPromptCompositionContext CompositionContext,
        SystemPromptDocument Document,
        object Resources) : AgentHarnessOwnEvent;

    public sealed record Context(
        IReadOnlyList<AgentMessage> Messages) : AgentHarnessOwnEvent;

    public sealed record BeforeProviderRequest(
        object Model,
        string SessionId,
        object StreamOptions) : AgentHarnessOwnEvent;

    public sealed record BeforeProviderPayload(
        object Model,
        JsonElement Payload) : AgentHarnessOwnEvent;

    public sealed record AfterProviderResponse(
        int Status,
        IReadOnlyDictionary<string, string> Headers) : AgentHarnessOwnEvent;

    public sealed record ToolCall(
        string ToolCallId,
        string ToolName,
        IReadOnlyDictionary<string, object?> Arguments) : AgentHarnessOwnEvent;

    public sealed record ToolResult(
        string ToolCallId,
        string ToolName,
        IReadOnlyDictionary<string, object?> Arguments,
        IReadOnlyList<MessageContent> Content,
        object Details,
        bool IsError) : AgentHarnessOwnEvent;

    public sealed record SessionBeforeCompact(
        object Preparation,
        IReadOnlyList<object> BranchEntries,
        string? CustomInstructions,
        CancellationToken Signal) : AgentHarnessOwnEvent;

    public sealed record SessionCompact(
        object CompactionEntry,
        bool FromHook) : AgentHarnessOwnEvent;

    public sealed record SessionBeforeTree(
        object Preparation,
        CancellationToken Signal) : AgentHarnessOwnEvent;

    public sealed record SessionTree(
        string? NewLeafId,
        string? OldLeafId,
        object? SummaryEntry,
        bool? FromHook) : AgentHarnessOwnEvent;

    public sealed record ModelSelect(
        object Model,
        object? PreviousModel,
        string Source) : AgentHarnessOwnEvent;

    public sealed record ThinkingLevelSelect(
        ThinkingLevel Level,
        ThinkingLevel PreviousLevel) : AgentHarnessOwnEvent;

    public sealed record ResourcesUpdate(
        object Resources,
        object PreviousResources) : AgentHarnessOwnEvent;

    public sealed record RuntimeEvent(
        string Name,
        object? Payload) : AgentHarnessOwnEvent;

    public sealed record SkillExecutionStart(
        string Name,
        string? AdditionalInstructions,
        IReadOnlyList<string> Args) : AgentHarnessOwnEvent;

    public sealed record SkillExecutionEnd(
        string Name,
        string? AdditionalInstructions,
        IReadOnlyList<string> Args,
        object? Result,
        bool IsError,
        string? ErrorMessage = null) : AgentHarnessOwnEvent;

    /// <summary>
    /// Dedicated variant for the reserved <c>advisor_note</c> event name: produced when the
    /// custom-event lane carries an <see cref="ExtensionAdvisorEvent"/> payload under the
    /// <c>advisor_note</c> name (see <see cref="AgentSessionEvent.MapReservedCustomEvent"/>).
    /// Flattened to the daemon/client stream via
    /// <see cref="AgentSessionEvent.FromAdvisor(ExtensionAdvisorEvent)"/>.
    /// </summary>
    public sealed record AdvisorNote(
        ExtensionAdvisorEvent Event) : AgentHarnessOwnEvent;

    /// <summary>
    /// Server-originated informational line (startup checks, self-update output, package/skill
    /// change notices) rendered as a TUI system row. Produced client-side from the flat
    /// <c>system_message</c> event; never raised by the runtime itself.
    /// </summary>
    public sealed record SystemMessage(string Text, bool IsError = false) : AgentHarnessOwnEvent;

    /// <summary>
    /// Extension-originated session event pushed to harness subscribers and the
    /// daemon wire via <c>PublishOwnEventAsync</c>. The name is validated at
    /// publish time (snake_case <c>[a-z0-9_]{1,64}</c>, no core-event collision)
    /// and the payload must be JSON-serializable. Names reserved for a dedicated
    /// session event are mapped to their typed variant instead of being rejected
    /// (see <see cref="AgentSessionEvent.MapReservedCustomEvent"/>).
    /// </summary>
    public sealed record CustomEvent(
        string Name,
        object? Payload) : AgentHarnessOwnEvent;
}
