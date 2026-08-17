using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Continuity.Contracts;
using PiSharp.Extensions;
using PiSharp.Server.Runtime;
using PiSharp.Server.UiBridge;

namespace PiSharp.Server.Contracts;

public static class ServerCommandTypes
{
    public const string CreateSession = "create_session";
    public const string DisposeSession = "dispose_session";
    public const string Prompt = "prompt";
    public const string Steer = "steer";
    public const string FollowUp = "follow_up";
    public const string QueueNextTurn = "queue_next_turn";
    public const string Abort = "abort";
    public const string GetState = "get_state";
    public const string GetMessages = "get_messages";
    public const string ListSessions = "list_sessions";
    public const string SetModel = "set_model";
    public const string SetThinkingLevel = "set_thinking_level";
    public const string Compact = "compact";
    public const string NewSession = "new_session";
    public const string SwitchSession = "switch_session";
    public const string Shutdown = "shutdown";
    public const string Fork = "fork";
    public const string SetSessionName = "set_session_name";
    public const string Attach = "attach";
    public const string RunCommand = "run_command";
    public const string CompleteCommand = "complete_command";
    public const string ProcessInput = "process_input";
    public const string GetTheme = "get_theme";
    public const string GetSessionSnapshot = "get_session_snapshot";
    public const string GetForkMessages = "get_fork_messages";
    public const string GetExtensionLoadStatus = "get_extension_load_status";
    public const string GetExtensionShortcuts = "get_extension_shortcuts";
    public const string InvokeExtensionShortcut = "invoke_extension_shortcut";
    public const string GetExtensionRegistry = "get_extension_registry";
    public const string ResolveTool = "resolve_tool";
    public const string RenderToolCall = "render_tool_call";
    public const string RenderToolResult = "render_tool_result";
    public const string CycleThinkingLevel = "cycle_thinking_level";
    public const string GetAvailableModels = "get_available_models";
    public const string GetCommands = "get_commands";
    public const string GetLastAssistantText = "get_last_assistant_text";
    public const string GetStartupMessages = "get_startup_messages";
    public const string PostStartupChecks = "post_startup_checks";
    public const string UiResponse = "ui_response";
    public const string ListThemes = "list_themes";
    public const string SetTheme = "set_theme";
    public const string McpStatus = "mcp_status";
    public const string InstallExtension = "install_extension";
    public const string UpdateExtension = "update_extension";
    public const string RemoveExtension = "uninstall_extension";
    public const string ListInstalledExtensions = "list_installed_extensions";
    public const string ManageSkill = "manage_skill";
    public const string GetSkills = "get_skills";
    public const string SetPlanMode = "set_plan_mode";
    public const string GetPlanMode = "get_plan_mode";
    public const string GetMetrics = "get_metrics";
    public const string GetSessionStats = "get_session_stats";
    // --- P23: continuity daemon wire surface (plan C5 §4.9) ---
    public const string SetGoal = "set_goal";
    public const string GetGoal = "get_goal";
    public const string ScheduleJob = "schedule_job";
    public const string ListJobs = "list_jobs";
    public const string CancelJob = "cancel_job";
    public const string Autonomous = "autonomous";
    public const string GetContinuityState = "get_continuity_state";
}

public sealed record ServerCommandEnvelope(string Type, string? Id = null, string? ServerSessionId = null);

public sealed record ListSessionsCommand(
    string Type,
    string? Id = null,
    string? ServerSessionId = null,
    string? Cwd = null,
    string? SessionsRoot = null,
    bool AllCwds = false);

public sealed record CreateServerSessionRequest(
    string Cwd,
    string? Id = null,
    string? Type = null,
    string? SessionId = null,
    string? SessionIdOrPath = null,
    bool ContinueLatestForCwd = false,
    string? SessionsRoot = null,
    string? Provider = null,
    string? Model = null,
    ThinkingLevel? Thinking = null,
    IReadOnlyList<string>? ScopedModels = null,
    IReadOnlyList<string>? Tools = null,
    bool NoTools = false,
    bool NoBuiltinTools = false,
    IReadOnlyList<string>? Extensions = null,
    bool NoExtensions = false,
    bool NoTsExtensions = false,
    bool NoSkills = false,
    bool NoPromptTemplates = false,
    bool NoThemes = false,
    bool NoContextFiles = false);

public sealed record PromptCommand(string Type, string? Id, string ServerSessionId, string Message, IReadOnlyList<ImageContent>? Images = null);
public sealed record TextMessageCommand(string Type, string? Id, string ServerSessionId, string Message, bool TriggerIfIdle = false);
public sealed record SessionCommand(string Type, string? Id, string ServerSessionId);
public sealed record SetModelCommand(string Type, string? Id, string ServerSessionId, string Provider, string ModelId);
public sealed record SetThinkingLevelCommand(string Type, string? Id, string ServerSessionId, ThinkingLevel Level);
public sealed record CompactCommand(string Type, string? Id, string ServerSessionId, string? CustomInstructions = null);
public sealed record SwitchSessionCommand(string Type, string? Id, string ServerSessionId, string SessionIdOrPath);
public sealed record ForkSessionCommand(string Type, string? Id, string ServerSessionId, string? EntryId = null, string? NewSessionId = null);
public sealed record SetSessionNameCommand(string Type, string? Id, string ServerSessionId, string Name);
public sealed record AttachCommand(string Type, string? Id, string ServerSessionId, long SinceSequence = 0);
public sealed record ShutdownRequest(string? ConfirmationToken = null, bool Confirm = false);
public sealed record ShutdownResult(bool Stopped);
public sealed record AttachResult(string ServerSessionId, long FromSequence, long HeadSequence, bool Gap, int ReplayedCount);

public sealed record ServerResponse(string Type, string? Id, string Command, bool Success, object? Data = null, ServerError? Error = null)
{
    public static ServerResponse Ok(string? id, string command, object? data = null) => new("response", id, command, true, data);
    public static ServerResponse Fail(string? id, string command, string code, string message, object? details = null) => new("response", id, command, false, null, new ServerError(code, message, details));
}

public sealed record ServerError(string Code, string Message, object? Details = null);

public sealed record ServerSessionState(
    string ServerSessionId,
    string RuntimeSessionId,
    string? RuntimeSessionPath,
    string? SessionName,
    string Cwd,
    ModelDescriptor Model,
    ThinkingLevel ThinkingLevel,
    bool IsBusy,
    bool IsCompacting,
    int MessageCount,
    long HighWatermark = 0);

public sealed record ServerSessionCreated(string ServerSessionId, ServerSessionState State);
public sealed record ServerMessagesResult(string ServerSessionId, IReadOnlyList<AgentMessage> Messages);
public sealed record ServerSessionListResult(IReadOnlyList<ServerPersistedSession> Sessions);

public sealed record ServerPersistedSession(
    string Id,
    DateTimeOffset CreatedAt,
    string Cwd,
    string Path,
    string? ParentSessionPath,
    bool IsLive,
    string? ServerSessionId);

public sealed record ServerEventEnvelope(
    string Type,
    string ServerSessionId,
    long Sequence,
    DateTimeOffset Timestamp,
    AgentSessionEvent Event)
{
    public static ServerEventEnvelope FromFlat(string serverSessionId, long sequence, AgentSessionEvent @event, DateTimeOffset? timestamp = null)
        => new("event", serverSessionId, sequence, timestamp ?? DateTimeOffset.UtcNow, @event);
}

public sealed record RunCommandRequest(string Type, string? Id, string ServerSessionId, string Text, SlashCommandExecutionOptions? Options = null);
public sealed record CompleteCommandRequest(string Type, string? Id, string ServerSessionId, string Text);
public sealed record ProcessInputRequest(string Text, IReadOnlyList<ImageContent>? Images = null, string Source = "interactive");
public sealed record ProcessInputResult(bool Handled, string Text, IReadOnlyList<ImageContent>? Images = null);
public sealed record ResolveToolRequest(string Type, string? Id, string ServerSessionId, string Name);

/// <summary>
/// Request for <see cref="ServerCommandTypes.RenderToolCall"/> / <see cref="ServerCommandTypes.RenderToolResult"/>:
/// the client asks the daemon to render a tool call/result line for a registered extension tool that
/// implements <see cref="PiSharp.Agent.Core.Tools.IAgentToolRenderer"/>. The daemon answers
/// <c>{ lines: [...] }</c>; non-renderable or unknown tools answer <c>not_available</c> so the
/// client TUI falls back to its plain text rows.
/// </summary>
public sealed record RenderToolRequest(
    string Type,
    string? Id,
    string ServerSessionId,
    string Name,
    string ToolCallId,
    JsonElement Arguments,
    bool IsCall,
    bool IsError,
    bool IsExpanded,
    int Width);
public sealed record UiResponseCommand(string Type, string? Id, string ServerSessionId, string RequestId, object? Value = null, bool Cancelled = false);

/// <summary>Request for the <see cref="ServerCommandTypes.McpStatus"/> command (read-only, session-independent).</summary>
public sealed record McpStatusCommand(string Type, string? Id = null);

/// <summary>Server-side mirror of <c>PiSharp.Mcp.McpServerStatus</c> (the plugin lives in an app-base assembly the server cannot reference).</summary>
public sealed record McpServerStatusEntry(
    string Name,
    string Source,
    string State,
    int ToolCount = 0,
    string? LastError = null,
    string? ServerInfo = null,
    int? ReconnectAttempt = null);

/// <summary>Response payload for <see cref="ServerCommandTypes.McpStatus"/>: <c>{ servers: [...] }</c>.</summary>
public sealed record McpStatusResult(IReadOnlyList<McpServerStatusEntry> Servers);

// --- P04: extension package + managed-skill daemon command surface (GAP-55/GAP-56) ---

/// <summary>Request for <see cref="ServerCommandTypes.InstallExtension"/>: installs a package and hot-reloads extensions.</summary>
public sealed record InstallExtensionCommand(
    string Type,
    string? Id = null,
    string? ServerSessionId = null,
    string? Reference = null,
    bool Local = false,
    bool Force = false,
    bool Offline = false);

/// <summary>Request for <see cref="ServerCommandTypes.UpdateExtension"/> (maps 1:1 to <c>ExtensionPackageUpdateRequest</c>).</summary>
public sealed record UpdateExtensionCommand(
    string Type,
    string? Id = null,
    string? ServerSessionId = null,
    string? Source = null,
    bool Extensions = false,
    string? ExtensionSource = null,
    bool Force = false,
    bool Offline = false);

/// <summary>Request for <see cref="ServerCommandTypes.RemoveExtension"/>: removes an installed package and hot-reloads extensions.</summary>
public sealed record RemoveExtensionCommand(
    string Type,
    string? Id = null,
    string? ServerSessionId = null,
    string? Reference = null,
    bool Local = false);
// --- P24: extension registry wire surface (scout gap 8) ---

/// <summary>
/// Serializable projection of <see cref="PiSharp.Extensions.ExtensionRegistry"/> answered by
/// <see cref="ServerCommandTypes.GetExtensionRegistry"/>. The live registry holds delegate-bearing
/// registrations that serialize to empty objects; this DTO carries only wireable metadata. The
/// renderer/decorator rows are metadata-only — their invocation handlers are not wireable — so
/// clients reconstruct tools/shortcuts but never renderer/decorator handlers.
/// </summary>
public sealed record ExtensionRegistryWire(
    IReadOnlyList<ExtensionToolWire> Tools,
    IReadOnlyList<ExtensionShortcutWire> Shortcuts,
    IReadOnlyList<ExtensionRendererWire> Renderers,
    IReadOnlyList<ExtensionDecoratorWire> Decorators);

/// <summary>Serializable tool projection for <see cref="ExtensionRegistryWire"/>.</summary>
public sealed record ExtensionToolWire(
    string Name, string Label, string Description,
    JsonElement ParametersSchema, bool HasRenderCall, bool HasRenderResult,
    string? RendererName, string? RenderShell,
    ToolExecutionMode? ExecutionMode, string? PromptSnippet, IReadOnlyList<string>? PromptGuidelines);

/// <summary>Serializable shortcut projection for <see cref="ExtensionRegistryWire"/>.</summary>
public sealed record ExtensionShortcutWire(string Id, string? SourceId, string Keys, string Description);

/// <summary>Serializable message-renderer projection for <see cref="ExtensionRegistryWire"/> (metadata only).</summary>
public sealed record ExtensionRendererWire(string RowType, string? CustomType, ExtensionOverridePolicy Override);

/// <summary>Serializable message-decorator projection for <see cref="ExtensionRegistryWire"/> (metadata only).</summary>
public sealed record ExtensionDecoratorWire(string RowType, string? CustomType, ExtensionOverridePolicy Override);

/// <summary>
/// Request for <see cref="ServerCommandTypes.InvokeExtensionShortcut"/>: invokes the registered
/// extension shortcut matching <see cref="Keys"/> on the live session with <see cref="Args"/>.
/// </summary>
public sealed record InvokeExtensionShortcutRequest(string Type, string? Id, string ServerSessionId, string Keys, string Args);


/// <summary>
/// Request for <see cref="ServerCommandTypes.ManageSkill"/>. <see cref="Op"/> is one of
/// <c>create</c>|<c>update</c>|<c>delete</c>|<c>list</c>|<c>promote</c>; the fields used per op:
/// create = Name/Description/Content/DisableModelInvocation, update = Name + optional fields,
/// delete = Name, promote = SourceReference.
/// </summary>
public sealed record ManageSkillCommand(
    string Type,
    string? Id = null,
    string? ServerSessionId = null,
    string Op = "list",
    string? Name = null,
    string? Description = null,
    string? Content = null,
    bool? DisableModelInvocation = null,
    string? SourceReference = null);

/// <summary>Serializable skill projection for <see cref="ServerCommandTypes.GetSkills"/> (the registry stores richer definitions with a runner delegate).</summary>
public sealed record ServerSkillInfo(
    string Name,
    string Description,
    string? Source,
    int SourcePriority,
    bool Hide,
    bool AlwaysApply,
    bool DisableModelInvocation,
    IReadOnlyList<string>? Globs = null);

/// <summary>Server-side equivalent of the CLI's slash-command execution options (which lives in PiSharp.Cli and cannot be referenced from the server).</summary>
public sealed record SlashCommandExecutionOptions(string? Cwd = null);

/// <summary>Server-side mirror of the CLI's <c>SlashCommandResult</c> for remote <c>run_command</c> dispatch.</summary>
public sealed record ServerCommandResult(bool Handled, string? Message = null, bool IsError = false, bool ShouldExit = false);

/// <summary>Server-side mirror of the TUI's <c>ExtensionUiIntent</c> (PiSharp.Tui cannot be referenced from the server).</summary>
public sealed record ServerUiIntent(
    string RequestId,
    string Kind,
    string Title,
    string? Message,
    IReadOnlyList<string>? Options,
    object? Component,
    string? ExtensionId = null);

public sealed record ServerUiResponse(string RequestId, object? Value = null, bool Cancelled = false);

public sealed record ServerStartupMessages(IReadOnlyList<string> Messages);

public sealed record ServerSessionSnapshot(
    string SessionId,
    string? SessionFile,
    string? SessionName,
    IReadOnlyList<object> BranchEntries,
    ModelDescriptor? Model = null,
    ThinkingLevel? ThinkingLevel = null);

/// <summary>Carries the live session and UI bridge into host-provided command delegates.</summary>
public sealed record PiServerHostContext(LiveServerSession Session, IServerUiBridge UiBridge);

/// <summary>
/// Host-wired command delegates consumed by <c>PiServerWebSocketHandler</c>. Each member mirrors the
/// corresponding <see cref="PiSharp.Server.Hosting.PiServerHostOptions"/> delegate; a null delegate
/// makes the command respond <c>not_available</c>.
public sealed record PiServerCommandDelegates(
    Func<PiServerHostContext, string, SlashCommandExecutionOptions?, CancellationToken, Task<ServerCommandResult>>? RunCommandAsync = null,
    Func<LiveServerSession, string, CancellationToken, Task<IReadOnlyList<string>>>? CompleteCommandAsync = null,
    Func<ProcessInputRequest, CancellationToken, Task<ProcessInputResult>>? ProcessInputAsync = null,
    Func<LiveServerSession, CancellationToken, Task<ServerStartupMessages>>? GetStartupMessagesAsync = null,
    Func<LiveServerSession, Action<string>, CancellationToken, Task>? PostStartupChecksAsync = null,
    Func<CancellationToken, Task<McpStatusResult>>? GetMcpStatusAsync = null,
    Func<LiveServerSession, CancellationToken, Task<IReadOnlyList<string>>>? GetCommandsAsync = null,
    Func<CancellationToken, Task>? OnShutdown = null);

// --- P05: daemon theme authority surface (plan C8) ---

/// <summary>
/// Request for <see cref="ServerCommandTypes.ListThemes"/>: names of all theme documents the daemon
/// registry currently knows (documents ride <c>get_theme</c> and <c>theme_changed</c>). The server
/// session id is optional — the theme registry is daemon-global, not session-scoped.
/// </summary>
public sealed record ListThemesCommand(string Type, string? Id = null, string? ServerSessionId = null);

/// <summary>
/// Request for <see cref="ServerCommandTypes.SetTheme"/>: activates the named theme in the daemon
/// registry and broadcasts <c>theme_changed</c> on every live session stream. The server session id
/// is optional — the active theme is daemon-global.
/// </summary>
public sealed record SetThemeCommand(string Type, string? Id = null, string? ServerSessionId = null, string? Name = null);

/// <summary>Name-only projection for <see cref="ServerCommandTypes.ListThemes"/>: <c>{ name }</c>.</summary>
public sealed record ServerThemeSummary(string Name);

// --- P14: plan-mode daemon surface (plan C5) ---

/// <summary>
/// Request for <see cref="ServerCommandTypes.SetPlanMode"/>: drives the plugin-owned plan-mode
/// machine. <see cref="Phase"/> is one of <c>planning</c> (enter), <c>executing</c> (approve),
/// <c>aborted</c> (abort), or <c>inactive</c> (end); the response carries the new
/// <see cref="ServerPlanModeState"/>. Each transition emits <c>plan_mode_changed</c> on the
/// session event stream via the C3 custom-event lane.
/// </summary>
public sealed record SetPlanModeCommand(string Type, string? Id, string ServerSessionId, string Phase);

/// <summary>
/// Wire snapshot of the plan-mode machine (mirror of the plugin's <c>PlanModeState</c>) returned by
/// <see cref="ServerCommandTypes.SetPlanMode"/> and <see cref="ServerCommandTypes.GetPlanMode"/>.
/// </summary>
public sealed record ServerPlanModeState(
    string Phase,
    IReadOnlyList<string> RestrictedToolNames,
    string? PlanningModel,
    string? PlanFile);

// --- P25: daemon observability surface (plan C6) ---

/// <summary>Per-model aggregation row inside <see cref="MetricsSnapshot"/>.</summary>
public sealed record PerModelMetrics(
    string Model,
    long Turns,
    long TokensIn,
    long TokensOut,
    long TokensCache,
    double AvgLatencyMs);

/// <summary>Per-tool aggregation row inside <see cref="MetricsSnapshot"/>.</summary>
public sealed record PerToolMetrics(
    string Tool,
    long Calls,
    long Failures,
    long Retries,
    double AvgDurationMs);

/// <summary>
/// Daemon-global telemetry aggregate returned by <see cref="ServerCommandTypes.GetMetrics"/>.
/// When telemetry is disabled (<see cref="Enabled"/> false) every counter is zero and the
/// breakdown lists are empty; <c>pisharp stats --json</c> renders this document verbatim.
/// </summary>
public sealed record MetricsSnapshot(
    bool Enabled,
    DateTimeOffset GeneratedAt,
    long SessionCount,
    long TurnCount,
    long TokenCountIn,
    long TokenCountOut,
    long TokenCountCache,
    long ToolCallCount,
    long ToolFailureCount,
    long ToolRetryCount,
    long CompactionCount,
    double TurnLatencyAvgMs,
    double TurnLatencyP95Ms,
    IReadOnlyList<PerModelMetrics> ByModel,
    IReadOnlyList<PerToolMetrics> ByTool,
    IReadOnlyList<string> RecentJournalLines)
{
    /// <summary>The canonical <c>get_metrics</c> payload when telemetry is disabled (plan §4.5).</summary>
    public static MetricsSnapshot Disabled(DateTimeOffset generatedAt)
        => new(false, generatedAt, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
}

/// <summary>
/// Per-session counters returned by <see cref="ServerCommandTypes.GetSessionStats"/>, computed
/// from the live session's message context and retained event log (no telemetry dependency).
/// </summary>
public sealed record SessionStats(
    string ServerSessionId,
    string RuntimeSessionId,
    string? Model,
    int Messages,
    int Turns,
    long TokensIn,
    long TokensOut,
    int ToolCalls,
    int ToolFailures,
    int Retries,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt);

// --- P23: continuity daemon wire surface request records (plan C5 §4.9) ---
// Response/result records (ContinuityGoalResult, ContinuityJobResult, etc.) live in
// PiSharp.Continuity.Contracts so the plugin, server, and client share one serialization shape.

/// <summary>Request for <see cref="ServerCommandTypes.SetGoal"/>: { Objective, MaxTokens? }.</summary>
public sealed record SetGoalCommand(string Type, string? Id, string ServerSessionId, string Objective, long? MaxTokens = null);

/// <summary>Request for <see cref="ServerCommandTypes.GetGoal"/>: {}.</summary>
public sealed record GetGoalCommand(string Type, string? Id, string ServerSessionId);

/// <summary>Request for <see cref="ServerCommandTypes.ScheduleJob"/>: { Name, Cron, Prompt, Enabled? }.</summary>
public sealed record ScheduleJobCommand(
    string Type,
    string? Id,
    string ServerSessionId,
    string Name,
    string Cron,
    string Prompt,
    bool? Enabled = null);

/// <summary>Request for <see cref="ServerCommandTypes.ListJobs"/>: {}.</summary>
public sealed record ListJobsCommand(string Type, string? Id, string ServerSessionId);

/// <summary>Request for <see cref="ServerCommandTypes.CancelJob"/>: { JobId }.</summary>
public sealed record CancelJobCommand(string Type, string? Id, string ServerSessionId, string JobId);

/// <summary>Request for <see cref="ServerCommandTypes.Autonomous"/>: { Message?, MaxTurns?, MaxTokens?, TimeoutMinutes?, Gates? }.</summary>
public sealed record AutonomousCommandRequest(
    string Type,
    string? Id,
    string ServerSessionId,
    string? Message = null,
    int? MaxTurns = null,
    long? MaxTokens = null,
    int? TimeoutMinutes = null,
    IReadOnlyList<QualityGate>? Gates = null);

/// <summary>Request for <see cref="ServerCommandTypes.GetContinuityState"/>: {}.</summary>
public sealed record GetContinuityStateCommand(string Type, string? Id, string ServerSessionId);
