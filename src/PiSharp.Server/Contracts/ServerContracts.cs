using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
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
    public const string GetExtensionRegistry = "get_extension_registry";
    public const string ResolveTool = "resolve_tool";
    public const string CycleThinkingLevel = "cycle_thinking_level";
    public const string GetAvailableModels = "get_available_models";
    public const string GetCommands = "get_commands";
    public const string GetLastAssistantText = "get_last_assistant_text";
    public const string GetStartupMessages = "get_startup_messages";
    public const string PostStartupChecks = "post_startup_checks";
    public const string UiResponse = "ui_response";
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
public sealed record ShutdownRequest(string? ConfirmationToken = null);
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
public sealed record UiResponseCommand(string Type, string? Id, string ServerSessionId, string RequestId, string? Value = null, bool Cancelled = false);

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

public sealed record ServerSessionSnapshot(string SessionId, string? SessionFile, string? SessionName, IReadOnlyList<object> BranchEntries);

/// <summary>Carries the live session and UI bridge into host-provided command delegates.</summary>
public sealed record PiServerHostContext(LiveServerSession Session, IServerUiBridge UiBridge);

/// <summary>
/// Host-wired command delegates consumed by <c>PiServerWebSocketHandler</c>. Each member mirrors the
/// corresponding <see cref="PiSharp.Server.Hosting.PiServerHostOptions"/> delegate; a null delegate
/// makes the command respond <c>not_available</c>.
public sealed record PiServerCommandDelegates(
    Func<PiServerHostContext, string, SlashCommandExecutionOptions?, CancellationToken, Task<ServerCommandResult>>? RunCommandAsync = null,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>>? CompleteCommandAsync = null,
    Func<ProcessInputRequest, CancellationToken, Task<ProcessInputResult>>? ProcessInputAsync = null,
    Func<CancellationToken, Task<ServerStartupMessages>>? GetStartupMessagesAsync = null,
    Func<Action<string>, CancellationToken, Task>? PostStartupChecksAsync = null,
    Func<CancellationToken, Task>? OnShutdown = null);
