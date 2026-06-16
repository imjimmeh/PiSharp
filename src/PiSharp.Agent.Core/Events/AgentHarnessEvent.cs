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
            _ => throw new NotSupportedException($"Unknown own event: {ownEvent.GetType().Name}")
        };

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
}
