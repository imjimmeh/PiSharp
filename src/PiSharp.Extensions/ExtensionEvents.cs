using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Extensions;

public static class ExtensionEventNames
{
    public const string SessionStart = "session_start";
    public const string ResourcesDiscover = "resources_discover";
    public const string Input = "input";
    public const string UserBash = "user_bash";
    public const string SessionBeforeSwitch = "session_before_switch";
    public const string SessionBeforeFork = "session_before_fork";
    public const string SessionShutdown = "session_shutdown";
    public const string AgentStart = "agent_start";
    public const string AgentEnd = "agent_end";
    public const string TurnStart = "turn_start";
    public const string TurnEnd = "turn_end";
    public const string MessageStart = "message_start";
    public const string MessageUpdate = "message_update";
    public const string MessageEnd = "message_end";
    public const string ToolExecutionStart = "tool_execution_start";
    public const string ToolExecutionUpdate = "tool_execution_update";
    public const string ToolExecutionEnd = "tool_execution_end";
    public const string QueueUpdate = "queue_update";
    public const string CompactionStart = "compaction_start";
    public const string CompactionEnd = "compaction_end";
    public const string AutoRetryStart = "auto_retry_start";
    public const string AutoRetryEnd = "auto_retry_end";
    public const string SessionInfoChanged = "session_info_changed";
    public const string ThinkingLevelChanged = "thinking_level_changed";
    public const string SavePoint = "save_point";
    public const string Abort = "abort";
    public const string Settled = "settled";
    public const string BeforeAgentStart = "before_agent_start";
    public const string BeforePromptRender = "before_prompt_render";
    public const string Context = "context";
    public const string BeforeProviderRequest = "before_provider_request";
    public const string BeforeProviderPayload = "before_provider_payload";
    public const string AfterProviderResponse = "after_provider_response";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string SessionBeforeCompact = "session_before_compact";
    public const string SessionCompact = "session_compact";
    public const string SessionBeforeTree = "session_before_tree";
    public const string SessionTree = "session_tree";
    public const string ModelSelect = "model_select";
    public const string ThinkingLevelSelect = "thinking_level_select";
    public const string SettingsChanged = "settings_changed";
    public const string ResourcesUpdate = "resources_update";
    public const string AdvisorNote = "advisor_note";
    public const string PackagesChanged = "extensions_changed";   // package install/update/remove (daemon-facing name)
    public const string SkillsChanged = "skills_changed";          // skill set changed (register/discover/managed store)
    public const string SkillExecutionStart = "skill_execution_start";
    public const string SkillExecutionEnd = "skill_execution_end";
}

public sealed record ExtensionInputEvent(string Text, IReadOnlyList<ImageContent>? Images = null, string Source = "runtime");
public sealed record ExtensionInputResult(string Action = "continue", string? Text = null, IReadOnlyList<ImageContent>? Images = null);
public sealed record ExtensionSessionBeforeSwitchEvent(string Reason, string? TargetSessionFile = null, object? CurrentSession = null, object? TargetSession = null);
public sealed record ExtensionSessionBeforeForkEvent(string EntryId, string Position, object? SourceSession = null, object? ForkOptions = null);
public sealed record ExtensionSessionShutdownEvent(string Reason, string? TargetSessionFile = null, object? Session = null);
public sealed record ExtensionSessionChangeResult(bool Cancel = false, string? Reason = null);

public sealed record ExtensionEvent(string Name, AgentHarnessEvent OriginalEvent, object? Payload = null)
{
    public string? ModifiedSystemPrompt { get; private set; }
    public IReadOnlyList<AgentMessage>? ModifiedMessages { get; private set; }
    public SystemPromptDocument? ModifiedPromptDocument { get; private set; }
    public PromptDocumentPatch? ModifiedPromptDocumentPatch { get; private set; }
    public ExtensionInputResult? InputResult { get; private set; }
    public ExtensionSessionChangeResult? SessionChangeResult { get; private set; }
    public ExtensionResourcesDiscoverResult? ResourcesDiscoverResult { get; private set; }
    public ExtensionUserBashResult? UserBashResult { get; private set; }

    public void ModifyBeforeAgentStart(string? systemPrompt = null, IReadOnlyList<AgentMessage>? messages = null)
    {
        if (systemPrompt is not null) ModifiedSystemPrompt = systemPrompt;
        if (messages is not null) ModifiedMessages = messages;
    }

    public void TransformInput(string text, IReadOnlyList<ImageContent>? images = null)
        => InputResult = new ExtensionInputResult("transform", text, images);

    public void HandleInput()
        => InputResult = new ExtensionInputResult("handled");

    public void CancelSessionChange(string? reason = null)
        => SessionChangeResult = new ExtensionSessionChangeResult(true, reason);

    public void ModifyPromptDocument(SystemPromptDocument document)
    {
        ModifiedPromptDocument = document;
        ModifiedPromptDocumentPatch = null;
    }

    public void ModifyPromptDocument(PromptDocumentPatch patch)
    {
        ModifiedPromptDocumentPatch = MergePatches(ModifiedPromptDocumentPatch, patch);
    }

    public void SetUserBashResult(ExtensionBashOperations? operations = null, ExtensionBashResult? result = null)
        => UserBashResult = new ExtensionUserBashResult(operations, result);

    public void AddResourcesDiscoverPaths(
        IReadOnlyList<string>? skillPaths = null,
        IReadOnlyList<string>? promptPaths = null,
        IReadOnlyList<string>? themePaths = null)
    {
        var existing = ResourcesDiscoverResult ?? new ExtensionResourcesDiscoverResult([], [], []);
        ResourcesDiscoverResult = new ExtensionResourcesDiscoverResult(
            SkillPaths: existing.SkillPaths.Concat(skillPaths ?? []).Distinct(StringComparer.Ordinal).ToArray(),
            PromptPaths: existing.PromptPaths.Concat(promptPaths ?? []).Distinct(StringComparer.Ordinal).ToArray(),
            ThemePaths: existing.ThemePaths.Concat(themePaths ?? []).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static PromptDocumentPatch MergePatches(PromptDocumentPatch? existing, PromptDocumentPatch next)
        => existing is null
            ? next
            : new PromptDocumentPatch(
                RemoveSectionIds: (existing.RemoveSectionIds ?? []).Concat(next.RemoveSectionIds ?? []).ToArray(),
                ReplaceSections: (existing.ReplaceSections ?? []).Concat(next.ReplaceSections ?? []).ToArray(),
                AppendSections: (existing.AppendSections ?? []).Concat(next.AppendSections ?? []).ToArray());
}

public sealed class ExtensionMiddlewareContext(
    ExtensionEvent Event,
    BeforeToolCallContext? BeforeToolCall = null,
    AfterToolCallContext? AfterToolCall = null)
{
    public ExtensionEvent Event { get; } = Event;
    public BeforeToolCallContext? BeforeToolCall { get; } = BeforeToolCall;
    public AfterToolCallContext? AfterToolCall { get; } = AfterToolCall;
    public bool Blocked { get; set; }
    public string? BlockReason { get; set; }
    public bool Modified { get; private set; }
    public IReadOnlyList<MessageContent>? ModifiedContent { get; private set; }
    public object? ModifiedDetails { get; private set; }
    public bool? IsError { get; private set; }

    public void ModifyToolResult(IReadOnlyList<MessageContent>? content = null, object? details = null, bool? isError = null)
    {
        Modified = true;
        ModifiedContent = content;
        ModifiedDetails = details;
        IsError = isError;
    }
}

public delegate Task ExtensionEventHandler(ExtensionEvent evt, CancellationToken cancellationToken);
public delegate Task ExtensionNext(ExtensionMiddlewareContext context, CancellationToken cancellationToken);
public delegate Task ExtensionMiddleware(ExtensionMiddlewareContext context, ExtensionNext next, CancellationToken cancellationToken);

public static class ExtensionEventMapper
{
    public static ExtensionEvent Map(AgentHarnessEvent evt) => new(Name(evt), evt, Payload(evt));

    public static string Name(AgentHarnessEvent evt) => evt switch
    {
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionStart } => ExtensionEventNames.SessionStart,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.Input } => ExtensionEventNames.Input,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeSwitch } => ExtensionEventNames.SessionBeforeSwitch,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeFork } => ExtensionEventNames.SessionBeforeFork,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionShutdown } => ExtensionEventNames.SessionShutdown,
        AgentHarnessEvent.Core { Event: AgentEvent.AgentStart } => ExtensionEventNames.AgentStart,
        AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd } => ExtensionEventNames.AgentEnd,
        AgentHarnessEvent.Core { Event: AgentEvent.TurnStart } => ExtensionEventNames.TurnStart,
        AgentHarnessEvent.Core { Event: AgentEvent.TurnEnd } => ExtensionEventNames.TurnEnd,
        AgentHarnessEvent.Core { Event: AgentEvent.MessageStart } => ExtensionEventNames.MessageStart,
        AgentHarnessEvent.Core { Event: AgentEvent.MessageUpdate } => ExtensionEventNames.MessageUpdate,
        AgentHarnessEvent.Core { Event: AgentEvent.MessageEnd } => ExtensionEventNames.MessageEnd,
        AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionStart } => ExtensionEventNames.ToolExecutionStart,
        AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionUpdate } => ExtensionEventNames.ToolExecutionUpdate,
        AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionEnd } => ExtensionEventNames.ToolExecutionEnd,
        AgentHarnessEvent.Core { Event: AgentEvent.AutoRetryStart } => ExtensionEventNames.AutoRetryStart,
        AgentHarnessEvent.Core { Event: AgentEvent.AutoRetryEnd } => ExtensionEventNames.AutoRetryEnd,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.QueueUpdate } => ExtensionEventNames.QueueUpdate,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionStart } => ExtensionEventNames.CompactionStart,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionEnd } => ExtensionEventNames.CompactionEnd,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.AutoRetryStart } => ExtensionEventNames.AutoRetryStart,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.AutoRetryEnd } => ExtensionEventNames.AutoRetryEnd,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionInfoChanged } => ExtensionEventNames.SessionInfoChanged,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelChanged } => ExtensionEventNames.ThinkingLevelChanged,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SavePoint } => ExtensionEventNames.SavePoint,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.Abort } => ExtensionEventNames.Abort,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.Settled } => ExtensionEventNames.Settled,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforeAgentStart } => ExtensionEventNames.BeforeAgentStart,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforePromptRender } => ExtensionEventNames.BeforePromptRender,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.Context } => ExtensionEventNames.Context,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforeProviderRequest } => ExtensionEventNames.BeforeProviderRequest,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforeProviderPayload } => ExtensionEventNames.BeforeProviderPayload,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.AfterProviderResponse } => ExtensionEventNames.AfterProviderResponse,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ToolCall } => ExtensionEventNames.ToolCall,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ToolResult } => ExtensionEventNames.ToolResult,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeCompact } => ExtensionEventNames.SessionBeforeCompact,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionCompact } => ExtensionEventNames.SessionCompact,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeTree } => ExtensionEventNames.SessionBeforeTree,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionTree } => ExtensionEventNames.SessionTree,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ModelSelect } => ExtensionEventNames.ModelSelect,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelSelect } => ExtensionEventNames.ThinkingLevelSelect,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ResourcesUpdate } => ExtensionEventNames.ResourcesUpdate,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.RuntimeEvent runtimeEvent } => runtimeEvent.Name,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SkillExecutionStart } => ExtensionEventNames.SkillExecutionStart,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SkillExecutionEnd } => ExtensionEventNames.SkillExecutionEnd,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CustomEvent customEvent } => customEvent.Name,
        _ => throw new NotSupportedException($"Unsupported extension event '{evt.GetType().Name}'.")
    };

    private static object? Payload(AgentHarnessEvent evt) => evt switch
    {
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.Input input } => new ExtensionInputEvent(input.Text, input.Images, input.Source),
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionShutdown shutdown } => new ExtensionSessionShutdownEvent(shutdown.Reason, shutdown.TargetSessionFile, shutdown.Session),
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.RuntimeEvent runtimeEvent } => runtimeEvent.Payload,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SkillExecutionStart start } => new { start.Name, start.AdditionalInstructions, start.Args },
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SkillExecutionEnd end } => new { end.Name, end.AdditionalInstructions, end.Args, end.Result, end.IsError, end.ErrorMessage },
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeSwitch beforeSwitch } => new ExtensionSessionBeforeSwitchEvent(beforeSwitch.Reason, beforeSwitch.TargetSessionFile, beforeSwitch.CurrentSession, beforeSwitch.TargetSession),
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CustomEvent customEvent } => customEvent.Payload,
        AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeFork beforeFork } => new ExtensionSessionBeforeForkEvent(beforeFork.EntryId, beforeFork.Position, beforeFork.SourceSession, beforeFork.ForkOptions),
        AgentHarnessEvent.Core core => core.Event,
        AgentHarnessEvent.Own own => own.Event,
        _ => null
    };
}
