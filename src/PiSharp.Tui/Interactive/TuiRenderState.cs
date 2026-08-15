using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Messages;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive;

public sealed record TuiTranscriptItem(
    string Role,
    string Text,
    bool IsStreaming = false,
    string? ToolName = null,
    bool IsError = false,
    string? ToolCallId = null,
    bool IsExpanded = false,
    UsageInfo? Usage = null,
    IReadOnlyList<MessageContent>? Content = null,
    JsonElement? ToolArguments = null,
    object? ToolResult = null,
    IReadOnlyList<string>? RenderedToolCall = null,
    IReadOnlyList<string>? RenderedToolResult = null,
    string? EntryId = null,
    bool IsPinnedToTop = false,
    DateTimeOffset? ExpiresAt = null,
    string? CustomType = null,
    object? CustomContent = null,
    object? CustomDetails = null,
    bool CustomDisplay = true,
    string? LocalId = null,
    string? SystemMessageTag = null,
    TimeSpan? RemoveDelayAfterEvent = null);

public sealed record TuiBridgeSlot(
    string Id,
    string Kind,
    string Title,
    string Content,
    bool Visible = true,
    string Placement = "above-editor",
    string? SourceId = null,
    TuiInteractionTarget? InteractionTarget = null);

public sealed record TuiMenuEntry(string Menu, string Label, string Command, string? Shortcut = null, string? SourceId = null);

public sealed record TuiRenderState(
    string SessionId,
    string? SessionFile,
    string? SessionName,
    string ModelDisplay,
    ThinkingLevel ThinkingLevel,
    bool IsBusy,
    string Status,
    IReadOnlyList<TuiTranscriptItem> Transcript,
    IReadOnlyList<TuiBridgeSlot> BridgeSlots,
    bool ShowThinking = false,
    bool ShowToolOutput = false,
    IReadOnlyDictionary<string, string>? ExtensionStatuses = null,
    string? TitleOverride = null,
    string? EditorText = null,
    int ContextWindow = 0,
    string? WorkingMessage = null,
    bool WorkingVisible = true,
    ExtensionWorkingIndicator? WorkingIndicator = null,
    int PendingMessageCount = 0,
    IReadOnlyList<SessionTreeEntry>? SessionBranchEntries = null,
    IReadOnlyList<TuiMenuEntry>? CustomMenuEntries = null,
    bool LeftSidebarVisible = true,
    bool RightSidebarVisible = true)
{
    public IReadOnlyDictionary<string, string> Statuses => ExtensionStatuses ?? new Dictionary<string, string>();
    public IReadOnlyList<TuiMenuEntry> CustomMenus => CustomMenuEntries ?? [];

    public static TuiRenderState Empty(string sessionId, string? sessionFile, ModelDescriptor model, ThinkingLevel thinkingLevel, string? sessionName)
        => new(sessionId, sessionFile, sessionName, DisplayModel(model), thinkingLevel, false, "Idle", [], [], ContextWindow: model.ContextWindow);

    public TuiRenderState AppendSystem(string text, bool isError = false, bool pinToTop = false,
        TimeSpan? expiresAfter = null, string? localId = null,
        string? systemMessageTag = null, TimeSpan? removeDelayAfterEvent = null)
        => TuiTranscriptReducer.AppendSystem(this, text, isError, pinToTop, expiresAfter, localId, systemMessageTag, removeDelayAfterEvent);

    public TuiRenderState RemoveExpiredSystemRows(DateTimeOffset now)
        => TuiTranscriptReducer.RemoveExpiredSystemRows(this, now);

    public TuiRenderState RestoreLocalSystemRows(IEnumerable<TuiTranscriptItem> rows)
        => TuiTranscriptReducer.RestoreLocalSystemRows(this, rows);

    public TuiRenderState ClearTranscript()
        => TuiTranscriptReducer.ClearTranscript(this);
    public TuiRenderState RemoveLocalSystemRow(string localId)
        => TuiTranscriptReducer.RemoveLocalSystemRow(this, localId);
    public TuiRenderState TriggerSystemMessageEvent(string eventTag, DateTimeOffset now)
        => TuiTranscriptReducer.TriggerSystemMessageEvent(this, eventTag, now);
    public TuiRenderState ToggleThinking() => this with { ShowThinking = !ShowThinking };
    public TuiRenderState SetToolOutput(bool visible) => this with { ShowToolOutput = visible };
    public TuiRenderState ToggleToolOutput() => this with { ShowToolOutput = !ShowToolOutput };

    public TuiRenderState ToggleToolExpanded(string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId)) return this;

        var copy = Transcript.ToArray();
        var index = Array.FindLastIndex(copy, item => string.Equals(item.Role, "tool", StringComparison.Ordinal)
            && string.Equals(item.ToolCallId, toolCallId, StringComparison.Ordinal));
        if (index < 0) return this;

        copy[index] = copy[index] with { IsExpanded = !copy[index].IsExpanded };
        return this with { Transcript = copy };
    }

    public TuiRenderState SetToolRenderedLines(string toolCallId, IReadOnlyList<string>? callLines = null, IReadOnlyList<string>? resultLines = null)
    {
        if (string.IsNullOrWhiteSpace(toolCallId)) return this;
        var copy = Transcript.ToArray();
        var index = Array.FindLastIndex(copy, item => string.Equals(item.Role, "tool", StringComparison.Ordinal)
            && string.Equals(item.ToolCallId, toolCallId, StringComparison.Ordinal));
        if (index < 0) return this;
        copy[index] = copy[index] with
        {
            RenderedToolCall = callLines ?? copy[index].RenderedToolCall,
            RenderedToolResult = resultLines ?? copy[index].RenderedToolResult
        };
        return this with { Transcript = copy };
    }

    public TuiRenderState SetEditorText(string? text) => this with { EditorText = text };
    public TuiRenderState SetTitle(string? title) => this with { TitleOverride = title };
    public TuiRenderState SetWorkingMessage(string? message) => this with { WorkingMessage = message };
    public TuiRenderState SetWorkingVisible(bool visible) => this with { WorkingVisible = visible };
    public TuiRenderState SetWorkingIndicator(ExtensionWorkingIndicator? indicator) => this with { WorkingIndicator = indicator };

    public TuiRenderState UpsertBridgeSlot(TuiBridgeSlot slot)
        => this with { BridgeSlots = BridgeSlots.Where(existing => existing.Id != slot.Id).Append(slot).ToArray() };

    public TuiRenderState RemoveBridgeSlot(string id)
        => this with { BridgeSlots = BridgeSlots.Where(existing => existing.Id != id).ToArray() };

    public TuiRenderState RemoveBridgeSlotsBySource(string sourceId)
        => this with
        {
            BridgeSlots = BridgeSlots.Where(slot => !StringComparer.Ordinal.Equals(slot.SourceId, sourceId)).ToArray(),
            ExtensionStatuses = Statuses.Where(pair => !StringComparer.Ordinal.Equals(pair.Key, sourceId)).ToDictionary(pair => pair.Key, pair => pair.Value)
        };

    /// <summary>
    /// Upserts or removes the <c>editor:{extensionId}</c> bridge slot that replaces the default
    /// prompt editor while present (see <c>TuiShellView</c>). A <c>null</c> component restores the
    /// built-in prompt editor.
    /// </summary>
    public TuiRenderState SetEditorComponent(string extensionId, ExtensionWidgetState? component)
    {
        if (component is null) return RemoveBridgeSlot($"editor:{extensionId}");

        return UpsertBridgeSlot(new TuiBridgeSlot(
            $"editor:{extensionId}",
            component.Kind,
            component.Title ?? extensionId,
            component.Content,
            Placement: "editor",
            SourceId: extensionId));
    }

    public TuiRenderState AddCustomMenuEntry(TuiMenuEntry entry)
        => this with { CustomMenuEntries = CustomMenus.Append(entry).ToArray() };

    public TuiRenderState RemoveCustomMenuEntriesBySource(string sourceId)
        => this with { CustomMenuEntries = CustomMenus.Where(e => !StringComparer.Ordinal.Equals(e.SourceId, sourceId)).ToArray() };

    public TuiRenderState ToggleLeftSidebar() => this with { LeftSidebarVisible = !LeftSidebarVisible };
    public TuiRenderState ToggleRightSidebar() => this with { RightSidebarVisible = !RightSidebarVisible };

    public TuiRenderState SetExtensionStatus(string extensionId, string? status)
    {
        var copy = Statuses.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(status)) copy.Remove(extensionId);
        else copy[extensionId] = status;
        return this with { ExtensionStatuses = copy };
    }

    public TuiRenderState HydrateSession(string sessionId, string? sessionFile, string? sessionName, IReadOnlyList<SessionTreeEntry> branchEntries)
    {
        var hydrated = this with
        {
            SessionId = sessionId,
            SessionFile = sessionFile,
            SessionName = sessionName,
            SessionBranchEntries = branchEntries,
            Transcript = []
        };

        foreach (var entry in branchEntries)
        {
            hydrated = entry switch
            {
                MessageEntry message => hydrated.UpsertMessageAndToolCalls(message.Message, streaming: false, message.Id),
                CustomMessageEntry { Display: true } custom => hydrated.Append(new TuiTranscriptItem("custom", CustomMessageContent.ToDisplayText(custom.Content), EntryId: custom.Id, CustomType: custom.CustomType, CustomContent: custom.Content, CustomDetails: custom.Details, CustomDisplay: custom.Display)),
                _ => hydrated
            };
        }

        return hydrated.PreserveToolUiStateFrom(this);
    }

    private TuiRenderState PreserveToolUiStateFrom(TuiRenderState previous)
    {
        var previousTools = previous.Transcript
            .Where(item => string.Equals(item.Role, "tool", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.ToolCallId))
            .GroupBy(item => item.ToolCallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        if (previousTools.Count == 0) return this;

        var copy = Transcript.Select(item =>
        {
            if (!string.Equals(item.Role, "tool", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.ToolCallId)
                || !previousTools.TryGetValue(item.ToolCallId, out var previousTool)) return item;

            return item with
            {
                IsExpanded = previousTool.IsExpanded,
                RenderedToolCall = item.RenderedToolCall ?? previousTool.RenderedToolCall,
                RenderedToolResult = item.RenderedToolResult ?? previousTool.RenderedToolResult
            };
        }).ToArray();
        return this with { Transcript = copy };
    }

    public TuiTranscriptItem? FindTranscriptItemByEntryId(string entryId)
        => TuiTranscriptReducer.FindTranscriptItemByEntryId(this, entryId);

    public TuiRenderState Reduce(AgentHarnessEvent evt)
    {
        var result = evt switch
        {
            AgentHarnessEvent.Core { Event: AgentEvent.AgentStart } => this with { IsBusy = true, Status = "Running" },
            AgentHarnessEvent.Core { Event: AgentEvent.TurnStart } => this with { IsBusy = true, Status = "Thinking" },
            AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd } => this with { IsBusy = false, Status = "Idle", PendingMessageCount = 0 },
            AgentHarnessEvent.Core { Event: AgentEvent.TurnEnd } => this with { IsBusy = false, Status = "Idle" },
            AgentHarnessEvent.Core { Event: AgentEvent.MessageStart start } => UpsertMessageAndToolCalls(start.Message, true),
            AgentHarnessEvent.Core { Event: AgentEvent.MessageUpdate update } => UpsertMessageAndToolCalls(update.Message, true),
            AgentHarnessEvent.Core { Event: AgentEvent.MessageEnd end } => UpsertMessageAndToolCalls(end.Message, false),
            AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionStart tool } => UpsertTool(new TuiTranscriptItem("tool", string.Empty, true, tool.ToolName, ToolCallId: tool.ToolCallId, ToolArguments: tool.Arguments.Clone())),
            AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionUpdate tool } => UpsertTool(new TuiTranscriptItem("tool", ToolResultText(tool.PartialResult), true, tool.ToolName, ToolCallId: tool.ToolCallId, ToolArguments: tool.Arguments.Clone(), ToolResult: tool.PartialResult)),
            AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionEnd tool } => UpsertTool(new TuiTranscriptItem("tool", ToolResultText(tool.Result), false, tool.ToolName, tool.IsError, tool.ToolCallId, ToolResult: tool.Result)),
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ModelSelect select } when select.Model is ModelDescriptor model => this with { ModelDisplay = DisplayModel(model), ContextWindow = model.ContextWindow },
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionStart } => this with { IsBusy = true, Status = "Compacting" },
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionEnd } => this with { IsBusy = false, Status = "Idle" },
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelSelect select } => this with { ThinkingLevel = select.Level },
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelChanged changed } => this with { ThinkingLevel = changed.Level },
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SystemMessage system } => AppendSystem(system.Text, system.IsError),
            _ => this
        };
        return evt is AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd }
            ? result
            : result with { PendingMessageCount = PendingMessageCount };
    }

    internal TuiRenderState ReduceBatch(IReadOnlyList<AgentHarnessEvent> events)
    {
        if (events.Count == 0) return this;

        var state = this;
        List<TuiTranscriptItem>? transcript = null;
        Dictionary<string, int>? toolIndexes = null;
        Dictionary<string, int>? streamingMessageIndexes = null;

        foreach (var evt in events)
        {
            switch (evt)
            {
                case AgentHarnessEvent.Core { Event: AgentEvent.AgentStart }:
                    state = state with { IsBusy = true, Status = "Running" };
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.TurnStart }:
                    state = state with { IsBusy = true, Status = "Thinking" };
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd }:
                    state = state with { IsBusy = false, Status = "Idle", PendingMessageCount = 0 };
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.TurnEnd }:
                    state = state with { IsBusy = false, Status = "Idle" };
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.MessageStart start }:
                    UpsertMessageAndToolCalls(start.Message, streaming: true);
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.MessageUpdate update }:
                    UpsertMessageAndToolCalls(update.Message, streaming: true);
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.MessageEnd end }:
                    UpsertMessageAndToolCalls(end.Message, streaming: false);
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionStart tool }:
                    UpsertTool(new TuiTranscriptItem("tool", string.Empty, true, tool.ToolName, ToolCallId: tool.ToolCallId, ToolArguments: tool.Arguments.Clone()));
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionUpdate tool }:
                    UpsertTool(new TuiTranscriptItem("tool", ToolResultText(tool.PartialResult), true, tool.ToolName, ToolCallId: tool.ToolCallId, ToolArguments: tool.Arguments.Clone(), ToolResult: tool.PartialResult));
                    break;
                case AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionEnd tool }:
                    UpsertTool(new TuiTranscriptItem("tool", ToolResultText(tool.Result), false, tool.ToolName, tool.IsError, tool.ToolCallId, ToolResult: tool.Result));
                    break;
                case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ModelSelect select } when select.Model is ModelDescriptor model:
                    state = state with { ModelDisplay = DisplayModel(model), ContextWindow = model.ContextWindow };
                    break;
                case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionStart }:
                    state = state with { IsBusy = true, Status = "Compacting" };
                    break;
                case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionEnd }:
                    state = state with { IsBusy = false, Status = "Idle" };
                    break;
                case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelSelect select }:
                    state = state with { ThinkingLevel = select.Level };
                    break;
                case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelChanged changed }:
                    state = state with { ThinkingLevel = changed.Level };
                    break;
                case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SystemMessage system }:
                    Append(new TuiTranscriptItem("system", system.Text, IsError: system.IsError));
                    break;
            }
        }

        return transcript is null ? state : state with { Transcript = transcript.ToArray() };

        List<TuiTranscriptItem> MutableTranscript()
        {
            if (transcript is not null) return transcript;
            transcript = state.Transcript.ToList();
            return transcript;
        }

        void Append(TuiTranscriptItem item)
        {
            var list = MutableTranscript();
            list.Add(item);
            var index = list.Count - 1;
            if (!string.IsNullOrWhiteSpace(item.ToolCallId)) ToolIndexes()[item.ToolCallId] = index;
            if (item.IsStreaming && item.ToolCallId is null) StreamingMessageIndexes()[item.Role] = index;
        }

        void UpsertTool(TuiTranscriptItem item)
        {
            var list = MutableTranscript();
            var index = FindToolIndex(item);
            if (index < 0)
            {
                Append(item);
                return;
            }

            var existing = list[index];
            var text = string.IsNullOrWhiteSpace(item.Text) && !string.IsNullOrWhiteSpace(existing.Text) ? existing.Text : item.Text;
            var toolName = string.IsNullOrWhiteSpace(item.ToolName) ? existing.ToolName : item.ToolName;
            var toolCallId = string.IsNullOrWhiteSpace(item.ToolCallId) ? existing.ToolCallId : item.ToolCallId;
            list[index] = item with
            {
                Text = text,
                ToolName = toolName,
                ToolCallId = toolCallId,
                IsExpanded = existing.IsExpanded,
                ToolArguments = item.ToolArguments ?? existing.ToolArguments,
                ToolResult = item.ToolResult ?? existing.ToolResult,
                RenderedToolCall = item.RenderedToolCall ?? existing.RenderedToolCall,
                RenderedToolResult = item.RenderedToolResult ?? existing.RenderedToolResult,
                EntryId = item.EntryId ?? existing.EntryId
            };

            if (!string.IsNullOrWhiteSpace(toolCallId)) ToolIndexes()[toolCallId] = index;
        }

        int FindToolIndex(TuiTranscriptItem item)
        {
            var list = MutableTranscript();
            if (string.IsNullOrWhiteSpace(item.ToolCallId)) return FindLastToolPlaceholder(list);
            if (ToolIndexes().TryGetValue(item.ToolCallId, out var index)) return index;

            index = FindLastToolPlaceholder(list);
            if (index >= 0) toolIndexes![item.ToolCallId] = index;
            return index;
        }

        int FindLastToolPlaceholder(IReadOnlyList<TuiTranscriptItem> list)
        {
            for (var index = list.Count - 1; index >= 0; index--)
            {
                if (IsToolPlaceholder(list[index])) return index;
            }

            return -1;
        }

        Dictionary<string, int> ToolIndexes()
        {
            if (toolIndexes is not null) return toolIndexes;
            var list = MutableTranscript();
            toolIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < list.Count; index++)
            {
                var toolCallId = list[index].ToolCallId;
                if (!string.IsNullOrWhiteSpace(toolCallId)) toolIndexes[toolCallId] = index;
            }

            return toolIndexes;
        }

        void UpsertMessageAndToolCalls(AgentMessage message, bool streaming, string? entryId = null)
        {
            if (message is ToolResultMessage toolResult)
            {
                UpsertTool(new TuiTranscriptItem("tool", ContentText(toolResult.Content), false, toolResult.ToolName, toolResult.IsError, toolResult.ToolUseId, ToolResult: toolResult, EntryId: entryId));
                return;
            }

            if (message is not AssistantMessage assistant || HasRenderableAssistantContent(assistant)) UpsertMessage(message, streaming, entryId);
            if (message is not AssistantMessage { Content: var content }) return;

            foreach (var tool in content.OfType<ToolCallContent>())
            {
                UpsertTool(new TuiTranscriptItem("tool", string.Empty, true, tool.Name, ToolCallId: tool.Id, ToolArguments: tool.Arguments.Clone(), EntryId: entryId));
            }
        }

        void UpsertMessage(AgentMessage message, bool streaming, string? entryId = null)
        {
            var item = CreateTranscriptItem(message, streaming, entryId);
            var list = MutableTranscript();
            var index = FindStreamingMessageIndex(item.Role);
            if (index < 0)
            {
                Append(item);
                return;
            }

            list[index] = item;
            if (item.IsStreaming) StreamingMessageIndexes()[item.Role] = index;
            else streamingMessageIndexes?.Remove(item.Role);
        }

        int FindStreamingMessageIndex(string role)
            => StreamingMessageIndexes().TryGetValue(role, out var index) ? index : -1;

        Dictionary<string, int> StreamingMessageIndexes()
        {
            if (streamingMessageIndexes is not null) return streamingMessageIndexes;
            var list = MutableTranscript();
            streamingMessageIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < list.Count; index++)
            {
                var item = list[index];
                if (item.IsStreaming && item.ToolCallId is null) streamingMessageIndexes[item.Role] = index;
            }

            return streamingMessageIndexes;
        }
    }

    private TuiRenderState Append(TuiTranscriptItem item) => this with { Transcript = Transcript.Concat([item]).ToArray() };

    private TuiRenderState UpsertTool(TuiTranscriptItem item)
    {
        var copy = Transcript.ToArray();
        var index = string.IsNullOrWhiteSpace(item.ToolCallId)
            ? Array.FindLastIndex(copy, IsToolPlaceholder)
            : Array.FindLastIndex(copy, existing => string.Equals(existing.ToolCallId, item.ToolCallId, StringComparison.Ordinal));

        if (index < 0 && !string.IsNullOrWhiteSpace(item.ToolCallId))
        {
            index = Array.FindLastIndex(copy, IsToolPlaceholder);
        }

        if (index < 0) return Append(item);

        var existing = copy[index];
        var text = string.IsNullOrWhiteSpace(item.Text) && !string.IsNullOrWhiteSpace(existing.Text) ? existing.Text : item.Text;
        var toolName = string.IsNullOrWhiteSpace(item.ToolName) ? existing.ToolName : item.ToolName;
        var toolCallId = string.IsNullOrWhiteSpace(item.ToolCallId) ? existing.ToolCallId : item.ToolCallId;
        copy[index] = item with
        {
            Text = text,
            ToolName = toolName,
            ToolCallId = toolCallId,
            IsExpanded = existing.IsExpanded,
            ToolArguments = item.ToolArguments ?? existing.ToolArguments,
            ToolResult = item.ToolResult ?? existing.ToolResult,
            RenderedToolCall = item.RenderedToolCall ?? existing.RenderedToolCall,
            RenderedToolResult = item.RenderedToolResult ?? existing.RenderedToolResult,
            EntryId = item.EntryId ?? existing.EntryId
        };
        return this with { Transcript = copy };
    }

    private static bool IsToolPlaceholder(TuiTranscriptItem item)
        => string.Equals(item.Role, "tool", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(item.ToolCallId);

    private TuiRenderState UpsertMessageAndToolCalls(AgentMessage message, bool streaming, string? entryId = null)
    {
        if (message is ToolResultMessage toolResult)
        {
            return UpsertTool(new TuiTranscriptItem("tool", ContentText(toolResult.Content), false, toolResult.ToolName, toolResult.IsError, toolResult.ToolUseId, ToolResult: toolResult, EntryId: entryId));
        }

        var state = this;
        if (message is not AssistantMessage assistant || HasRenderableAssistantContent(assistant)) state = state.UpsertMessage(message, streaming, entryId);
        if (message is not AssistantMessage { Content: var content }) return state;

        foreach (var tool in content.OfType<ToolCallContent>())
        {
            state = state.UpsertTool(new TuiTranscriptItem("tool", string.Empty, true, tool.Name, ToolCallId: tool.Id, ToolArguments: tool.Arguments.Clone(), EntryId: entryId));
        }

        return state;
    }

    private TuiRenderState UpsertMessage(AgentMessage message, bool streaming, string? entryId = null)
    {
        var item = CreateTranscriptItem(message, streaming, entryId);
        var copy = Transcript.ToArray();
        var index = Array.FindLastIndex(copy, existing => existing.Role == item.Role && existing.ToolCallId is null && existing.IsStreaming);
        if (index < 0) return Append(item);
        copy[index] = item;
        return this with { Transcript = copy };
    }

    private static bool HasRenderableAssistantContent(AssistantMessage message)
        => message.Content.Any(content => content switch
        {
            TextContent text => !string.IsNullOrWhiteSpace(text.Text),
            ThinkingContent => true,
            ImageContent => true,
            _ => false
        }) || !string.IsNullOrWhiteSpace(message.ErrorMessage);

    private static TuiTranscriptItem CreateTranscriptItem(AgentMessage message, bool streaming, string? entryId = null)
    {
        var text = ContentText(message);
        if (message is CustomMessage { Display: false })
            return new TuiTranscriptItem("custom", string.Empty, EntryId: entryId, CustomDisplay: false);
        if (message is AssistantMessage { ErrorMessage: { Length: > 0 } error } && string.IsNullOrWhiteSpace(text)) text = error;
        if (message is CustomMessage custom)
            return new TuiTranscriptItem(custom.Role, string.IsNullOrWhiteSpace(text) ? CustomMessageContent.ToDisplayText(custom.ContentBlocks ?? (object?)custom.TextContent) : text, streaming, Content: Content(message), EntryId: entryId, CustomType: custom.CustomType, CustomContent: custom.ContentBlocks ?? (object?)custom.TextContent, CustomDetails: custom.Details);
        return new TuiTranscriptItem(message.Role, text, streaming, Usage: (message as AssistantMessage)?.Usage, Content: Content(message), IsError: message is AssistantMessage { ErrorMessage: not null }, EntryId: entryId);
    }

    private static string ToolResultText(object? result)
        => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement json => JsonText(json),
            ToolResultMessage toolResult => ContentText(toolResult.Content),
            _ => ContentFromToolResult(result) ?? SerializeObject(result)
        };

    private static string? ContentFromToolResult(object result)
    {
        var content = result.GetType().GetProperty("Content")?.GetValue(result) as IReadOnlyList<MessageContent>;
        if (content is null) return null;
        return ContentText(content);
    }

    private static string ContentText(AgentMessage message) => ContentText(Content(message));

    private static string ContentText(IEnumerable<MessageContent> content)
        => string.Concat(content.Select(item => item switch
        {
            TextContent text => text.Text,
            ThinkingContent thinking => thinking.Redacted ? "[redacted thinking]" : thinking.Thinking,
            ImageContent image => $"[image: {image.MediaType}]",
            _ => string.Empty
        }));

    private static string JsonText(JsonElement json)
        => json.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? string.Empty : json.GetRawText();

    private static string SerializeObject(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static IReadOnlyList<MessageContent> Content(AgentMessage message)
        => message switch
        {
            UserMessage user => user.Content,
            AssistantMessage assistant => assistant.Content,
            ToolResultMessage tool => tool.Content,
            CustomMessage custom => custom.ContentBlocks ?? [],
            _ => []
        };

    private static string DisplayModel(ModelDescriptor model) => string.IsNullOrWhiteSpace(model.Name) ? $"{model.Provider}/{model.Id}" : model.Name;
}
