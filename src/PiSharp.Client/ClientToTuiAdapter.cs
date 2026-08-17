using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Serialization;
using PiSharp.Server.Contracts;
using PiSharp.Tui.Interactive;

namespace PiSharp.Client;

/// <summary>
/// Maps between the client-side wire shapes (<see cref="ClientSessionState"/>,
/// <see cref="ServerEventEnvelope"/>) and the TUI's harness shapes (<see cref="TuiRenderState"/>,
/// <see cref="AgentHarnessEvent"/>). Pure and side-effect free.
/// </summary>
public static class ClientToTuiAdapter
{
    /// <summary>
    /// Derives the header/footer fields of a <see cref="TuiRenderState"/> from the client-side
    /// session state, keeping every other field of <paramref name="seed"/> (transcript rows flow
    /// through the reducer pipeline, not here). Used for the initial hydrate and gap-recovery resync.
    /// </summary>
    public static TuiRenderState ToRenderState(ClientSessionState state, TuiRenderState seed)
        => seed with
        {
            SessionName = state.SessionName ?? seed.SessionName,
            ModelDisplay = !string.IsNullOrWhiteSpace(state.ModelDisplay) ? state.ModelDisplay : seed.ModelDisplay,
            ThinkingLevel = TryParseThinkingLevel(state.ThinkingLevel) ?? seed.ThinkingLevel,
            IsBusy = state.IsBusy,
            Status = state.Status,
            ContextWindow = state.ContextWindow is { } window && window > 0 ? (int)window : seed.ContextWindow,
        };

    /// <summary>
    /// Inverse of <c>AgentSessionEvent.FromCore</c>/<c>FromOwn</c>: rebuilds the
    /// <see cref="AgentHarnessEvent"/> the server's flat event was produced from, so
    /// <c>TuiHarnessSubscription</c>'s existing reducer keeps rendering (message/tool rows, model and
    /// thinking-level header state). Returns <c>null</c> for server-defined types without a faithful
    /// inverse (<c>session_start</c>, <c>ui_request</c>, compaction, …) — callers log and skip.
    /// </summary>
    public static AgentHarnessEvent? ToHarnessEvent(ServerEventEnvelope envelope)
        => envelope.Event.Type switch
        {
            "agent_start" => new AgentHarnessEvent.Core(new AgentEvent.AgentStart()),
            "agent_end" => MapAgentEnd(envelope.Event.Data),
            "turn_start" => new AgentHarnessEvent.Core(new AgentEvent.TurnStart()),
            "turn_end" => MapTurnEnd(envelope.Event.Data),
            "message_start" => MapMessageStart(envelope.Event.Data),
            "message_update" => MapMessageUpdate(envelope.Event.Data),
            "message_end" => MapMessageEnd(envelope.Event.Data),
            "tool_execution_start" => MapToolStart(envelope.Event.Data),
            "tool_execution_update" => MapToolUpdate(envelope.Event.Data),
            "tool_execution_end" => MapToolEnd(envelope.Event.Data),
            "model_select" => MapModelSelect(envelope.Event.Data),
            "thinking_level_changed" => MapThinkingLevelChanged(envelope.Event.Data),
            "thinking_level_select" => MapThinkingLevelSelect(envelope.Event.Data),
            "compaction_start" => MapCompactionStart(envelope.Event.Data),
            "compaction_end" => MapCompactionEnd(envelope.Event.Data),
            "system_message" => MapSystemMessage(envelope.Event.Data),
            "theme_changed" => MapThemeChanged(envelope.Event.Data),
            "session_metrics" => MapSessionMetrics(envelope.Event.Data),
            "extension_load_status" => MapExtensionLoadStatus(envelope.Event.Data),
            "modified_files" => MapModifiedFiles(envelope.Event.Data),
            // The server emits package changes under ExtensionEventNames.PackagesChanged.
            "extensions_changed" => MapPackagesChanged(envelope.Event.Data),
            "skills_changed" => MapSkillsChanged(envelope.Event.Data),
            _ => null,
        };

    /// <summary>
    /// Folds a <c>get_state</c> snapshot into the client session state, preserving the accumulated
    /// transcript (the snapshot carries no messages — the retained replay redelivers those).
    /// </summary>
    public static ClientSessionState ToClientState(ClientSessionState current, ServerSessionState snapshot)
        => current with
        {
            IsBusy = snapshot.IsBusy,
            IsCompacting = snapshot.IsCompacting,
            Status = snapshot.IsCompacting ? "Compacting" : snapshot.IsBusy ? "Busy" : "Idle",
            ModelDisplay = DisplayModel(snapshot.Model),
            ContextWindow = snapshot.Model.ContextWindow > 0 ? snapshot.Model.ContextWindow : current.ContextWindow,
            ThinkingLevel = snapshot.ThinkingLevel.ToString().ToLowerInvariant(),
            LastAppliedSequence = snapshot.HighWatermark,
            SessionName = snapshot.SessionName ?? current.SessionName,
        };

    /// <summary>Extracts the selected <see cref="ModelDescriptor"/> from a <c>model_select</c> payload.</summary>
    internal static ModelDescriptor? ExtractModel(object? data)
        => FromPayload<ModelSelectPayload>(data)?.Model;

    /// <summary>Extracts the current <see cref="ThinkingLevel"/> from a thinking-level event payload.</summary>
    internal static ThinkingLevel? ExtractThinkingLevel(object? data)
        => FromPayload<ThinkingLevelPayload>(data) is { Level: { } level } payload
            ? TryParseThinkingLevel(level) ?? TryParseThinkingLevel(payload.PreviousLevel)
            : null;

    private static string DisplayModel(ModelDescriptor model)
        => !string.IsNullOrWhiteSpace(model.Name)
            ? model.Name
            : string.IsNullOrWhiteSpace(model.Provider)
                ? model.Id
                : $"{model.Provider}/{model.Id}";

    /// <summary>
    /// Maps the server's <see cref="ServerSessionSnapshot"/> to the TUI's
    /// <see cref="TuiSessionSnapshot"/> (branch entries are deserialized with the shared agent JSON
    /// options, which includes the <c>SessionTreeEntryJsonConverter</c>).
    /// </summary>
    public static TuiSessionSnapshot ToSessionSnapshot(ServerSessionSnapshot snapshot)
    {
        var entries = new List<SessionTreeEntry>(snapshot.BranchEntries.Count);
        foreach (var entry in snapshot.BranchEntries)
        {
            if (FromPayload<SessionTreeEntry>(entry) is { } parsed) entries.Add(parsed);
        }

        TuiFooterSnapshot? footer = null;
        if (snapshot.Footer is { } f)
        {
            footer = new TuiFooterSnapshot(
                f.Cwd,
                f.GitBranch,
                f.InputTokens,
                f.OutputTokens,
                f.CacheTokens,
                f.TotalTokens,
                f.TotalCost,
                f.ContextPercent,
                f.ContextWindow,
                f.AutoCompact)
            {
                ContextPercentKnown = f.ContextPercentKnown
            };
        }

        TuiExtensionLoadStatus? loadStatus = null;
        if (snapshot.ExtensionLoadStatus is { } ls)
        {
            loadStatus = new TuiExtensionLoadStatus(
                ls.Total,
                ls.Active,
                ls.BlockingActive,
                ls.Ready,
                ls.Failed);
        }

        return new TuiSessionSnapshot(
            snapshot.SessionId,
            snapshot.SessionFile,
            snapshot.SessionName,
            entries,
            snapshot.Model,
            snapshot.ThinkingLevel,
            footer,
            snapshot.ModifiedFiles,
            loadStatus,
            snapshot.Shortcuts,
            snapshot.Commands);
    }

    public static ThinkingLevel? TryParseThinkingLevel(string? value)
        => string.IsNullOrWhiteSpace(value) || !Enum.TryParse<ThinkingLevel>(value, ignoreCase: true, out var level)
            ? null
            : level;

    // --- core event inverses ---

    private static AgentHarnessEvent? MapAgentEnd(object? data)
        => FromPayload<AgentEndPayload>(data) is { } payload
            ? new AgentHarnessEvent.Core(new AgentEvent.AgentEnd(payload.Messages ?? []))
            : null;

    private static AgentHarnessEvent? MapTurnEnd(object? data)
        => FromPayload<TurnEndPayload>(data) is { Message: { } message } payload
            ? new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(message, payload.ToolResults ?? []))
            : null;

    private static AgentHarnessEvent? MapMessageStart(object? data)
        => FromPayload<MessagePayload>(data)?.Message is { } message
            ? new AgentHarnessEvent.Core(new AgentEvent.MessageStart(message))
            : null;

    private static AgentHarnessEvent? MapMessageUpdate(object? data)
    {
        if (FromPayload<MessageUpdatePayload>(data) is not { Message: { } message } payload) return null;
        return new AgentHarnessEvent.Core(new AgentEvent.MessageUpdate(message, BuildAssistantEvent(payload.AssistantMessageEvent)));
    }

    private static AgentHarnessEvent? MapMessageEnd(object? data)
        => FromPayload<MessagePayload>(data)?.Message is { } message
            ? new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(message))
            : null;

    private static AgentHarnessEvent? MapToolStart(object? data)
        => FromPayload<ToolExecutionStartPayload>(data) is { ToolCallId: not null } payload
            ? new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart(payload.ToolCallId, payload.ToolName ?? string.Empty, payload.Arguments))
            : null;

    private static AgentHarnessEvent? MapToolUpdate(object? data)
        => FromPayload<ToolExecutionUpdatePayload>(data) is { ToolCallId: not null } payload
            ? new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionUpdate(payload.ToolCallId, payload.ToolName ?? string.Empty, payload.Arguments, payload.PartialResult ?? string.Empty))
            : null;

    private static AgentHarnessEvent? MapToolEnd(object? data)
        => FromPayload<ToolExecutionEndPayload>(data) is { ToolCallId: not null } payload
            ? new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd(payload.ToolCallId, payload.ToolName ?? string.Empty, payload.Result ?? string.Empty, payload.IsError))
            : null;

    private static AgentHarnessEvent? MapModelSelect(object? data)
        => FromPayload<ModelSelectPayload>(data) is { Model: { } model } payload
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ModelSelect(model, payload.PreviousModel, payload.Source ?? "remote"))
            : null;

    private static AgentHarnessEvent? MapThinkingLevelChanged(object? data)
        => FromPayload<ThinkingLevelPayload>(data) is { Level: { } level } && TryParseThinkingLevel(level) is { } parsed
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ThinkingLevelChanged(parsed))
            : null;

    private static AgentHarnessEvent? MapThinkingLevelSelect(object? data)
        => FromPayload<ThinkingLevelPayload>(data) is { Level: { } level } payload && TryParseThinkingLevel(level) is { } parsed
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ThinkingLevelSelect(parsed, TryParseThinkingLevel(payload.PreviousLevel) ?? parsed))
            : null;

    private static AgentHarnessEvent? MapCompactionStart(object? data)
        => FromPayload<CompactionStartPayload>(data) is { } payload
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionStart(payload.Reason ?? string.Empty))
            : new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionStart(string.Empty));

    private static AgentHarnessEvent? MapCompactionEnd(object? data)
        => FromPayload<CompactionEndPayload>(data) is { } payload
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionEnd(
                payload.Reason ?? string.Empty,
                payload.Result,
                payload.Aborted ?? false,
                payload.WillRetry ?? false,
                payload.ErrorMessage))
            : null;

    private static AgentHarnessEvent? MapSystemMessage(object? data)
        => FromPayload<SystemMessageWire>(data) is { Text: { } text } wire && !string.IsNullOrWhiteSpace(text)
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SystemMessage(text, wire.IsError ?? false))
            : null;

    /// <summary>
    /// Maps <c>theme_changed</c> to an informational system row. Live re-theming is out of scope:
    /// no live re-theme path exists even locally (<c>TuiTheme.Apply</c> runs at startup only).
    /// </summary>
    private static AgentHarnessEvent? MapThemeChanged(object? data)
        => FromPayload<ThemeChangedWire>(data) is { Name: { Length: > 0 } name }
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SystemMessage($"Theme changed to '{name}'."))
            : null;

    private static AgentHarnessEvent? MapPackagesChanged(object? data) => MapListChanged(data, "Extensions");

    private static AgentHarnessEvent? MapSkillsChanged(object? data) => MapListChanged(data, "Skills");

    private static AgentHarnessEvent? MapListChanged(object? data, string noun)
    {
        var wire = FromPayload<ListChangedWire>(data);
        var parts = new List<string>();
        if (wire?.Added is { Count: > 0 } added) parts.Add($"added {string.Join(", ", added)}");
        if (wire?.Removed is { Count: > 0 } removed) parts.Add($"removed {string.Join(", ", removed)}");
        if (wire?.Updated is { Count: > 0 } updated) parts.Add($"updated {string.Join(", ", updated)}");
        return parts.Count == 0
            ? null
            : new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SystemMessage($"{noun} changed: {string.Join("; ", parts)}."));
    }

    // --- assistant event reconstruction ---

    private static AssistantMessageEvent BuildAssistantEvent(AssistantEventPayload? payload)
    {
        if (payload is null || payload.Type is null)
        {
            return new AssistantMessageEvent.Done(new AssistantMessage([], Timestamp: DateTimeOffset.UtcNow));
        }

        var partial = payload.Partial ?? new AssistantMessage([], Timestamp: DateTimeOffset.UtcNow);
        var contentIndex = payload.ContentIndex ?? 0;
        return payload.Type switch
        {
            "start" => new AssistantMessageEvent.Start(partial),
            "text_start" => new AssistantMessageEvent.TextStart(partial, contentIndex),
            "text_delta" => new AssistantMessageEvent.TextDelta(partial, contentIndex, payload.Delta ?? string.Empty),
            "text_end" => new AssistantMessageEvent.TextEnd(partial, contentIndex),
            "thinking_start" => new AssistantMessageEvent.ThinkingStart(partial, contentIndex),
            "thinking_delta" => new AssistantMessageEvent.ThinkingDelta(partial, contentIndex, payload.Delta ?? string.Empty),
            "thinking_end" => new AssistantMessageEvent.ThinkingEnd(partial, contentIndex),
            "tool_call_start" => new AssistantMessageEvent.ToolCallStart(partial, contentIndex),
            "tool_call_delta" => new AssistantMessageEvent.ToolCallDelta(partial, contentIndex, payload.Delta ?? string.Empty),
            "tool_call_end" => new AssistantMessageEvent.ToolCallEnd(partial, contentIndex, payload.ToolCall ?? new ToolCallContent(string.Empty, string.Empty, default)),
            "done" => new AssistantMessageEvent.Done(payload.Message ?? partial, payload.Reason),
            "error" => new AssistantMessageEvent.Error(payload.ErrorMessage ?? partial, payload.Reason),
            _ => new AssistantMessageEvent.Done(partial),
        };
    }

    // --- payload deserialization ---

    /// <summary>
    /// Round-trips the opaque <see cref="AgentSessionEvent.Data"/> (an anonymous payload object, or a
    /// <see cref="JsonElement"/> when the envelope arrived over the wire) into a typed payload record
    /// using the shared agent JSON options — same approach as <see cref="ClientEventReducer"/>.
    /// </summary>
    internal static T? FromPayload<T>(object? data)
    {
        if (data is null) return default;
        return AgentJsonSerializer.Deserialize<T>(AgentJsonSerializer.Serialize(data));
    }

    // Flat payload shapes mirroring the anonymous objects produced by AgentSessionEvent.FromCore/
    // FromOwn (camelCase via AgentJsonSerializer.Options). Property names match the wire exactly.
    private sealed record AgentEndPayload(IReadOnlyList<AgentMessage> Messages);
    private sealed record TurnEndPayload(AgentMessage? Message, IReadOnlyList<ToolResultMessage> ToolResults);
    private sealed record MessagePayload(AgentMessage? Message);
    private sealed record MessageUpdatePayload(AgentMessage? Message, AssistantEventPayload? AssistantMessageEvent);
    private sealed record AssistantEventPayload(
        string? Type,
        AssistantMessage? Partial,
        int? ContentIndex,
        string? Delta,
        ToolCallContent? ToolCall,
        AssistantMessage? Message,
        AssistantMessage? ErrorMessage,
        string? Reason);
    private sealed record ToolExecutionStartPayload(string ToolCallId, string? ToolName, JsonElement Arguments);
    private sealed record ToolExecutionUpdatePayload(string ToolCallId, string? ToolName, JsonElement Arguments, object? PartialResult);
    private sealed record ToolExecutionEndPayload(string ToolCallId, string? ToolName, object? Result, bool IsError);
    private sealed record ModelSelectPayload(ModelDescriptor? Model, ModelDescriptor? PreviousModel, string? Source);
    private sealed record ThinkingLevelPayload(string? Level, string? PreviousLevel);
    private sealed record CompactionStartPayload(string? Reason);
    private sealed record CompactionEndPayload(string? Reason, object? Result, bool? Aborted, bool? WillRetry, string? ErrorMessage);
    private sealed record SystemMessageWire(string? Text, bool? IsError);
    private sealed record ThemeChangedWire(string? Name, object? Document);
    private sealed record ListChangedWire(IReadOnlyList<string>? Added, IReadOnlyList<string>? Removed, IReadOnlyList<string>? Updated);
    private sealed record SessionMetricsWire(
        string? Cwd,
        string? GitBranch,
        int InputTokens,
        int OutputTokens,
        int CacheTokens,
        int TotalTokens,
        decimal TotalCost,
        double ContextPercent,
        bool ContextPercentKnown,
        int ContextWindow,
        bool AutoCompact);
    internal sealed record ExtensionLoadStatusWire(
        int Total,
        int Active,
        int BlockingActive,
        int Ready,
        int Failed,
        IReadOnlyList<AgentHarnessOwnEvent.ExtensionLoadDiagnosticRecord>? Failures);
    internal sealed record ModifiedFilesWire(IReadOnlyList<string>? Files);

    private static AgentHarnessEvent? MapSessionMetrics(object? data)
        => FromPayload<SessionMetricsWire>(data) is { } w
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionMetrics(
                w.Cwd ?? string.Empty,
                w.GitBranch,
                w.InputTokens,
                w.OutputTokens,
                w.CacheTokens,
                w.TotalTokens,
                w.TotalCost,
                w.ContextPercent,
                w.ContextPercentKnown,
                w.ContextWindow,
                w.AutoCompact))
            : null;

    private static AgentHarnessEvent? MapExtensionLoadStatus(object? data)
        => FromPayload<ExtensionLoadStatusWire>(data) is { } w
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ExtensionLoadStatusUpdate(
                w.Total,
                w.Active,
                w.BlockingActive,
                w.Ready,
                w.Failed,
                w.Failures))
            : null;

    private static AgentHarnessEvent? MapModifiedFiles(object? data)
        => FromPayload<ModifiedFilesWire>(data) is { Files: { } files }
            ? new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ModifiedFilesUpdate(files))
            : null;
}
