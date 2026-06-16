using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;

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
    public const string Fork = "fork";
    public const string SetSessionName = "set_session_name";
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
    int MessageCount);

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
