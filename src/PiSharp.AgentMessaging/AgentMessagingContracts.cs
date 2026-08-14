namespace PiSharp.AgentMessaging;

/// <summary>
/// Event-name constants for agent messaging. The plugin emits these on the
/// extension bus and pushes them to the daemon wire via the C3 custom-event
/// lane (<c>IExtensionApi.EmitClientEventAsync</c>), so clients render roster
/// and message rows from the per-session event stream.
/// </summary>
public static class AgentMessagingEventNames
{
    /// <summary>An agent-to-agent message (envelope, delivery notice, or inbox row).</summary>
    public const string AgentMessage = "agent_message";

    /// <summary>Roster membership/status refresh (full family roster per change).</summary>
    public const string AgentRosterUpdate = "agent_roster_update";
}

/// <summary>
/// Lifecycle status of a roster member. Mirrors the daemon's live-vs-passivated
/// taxonomy: a <see cref="Running"/> agent is actively hosted, a
/// <see cref="Passivated"/> agent's warm wrapper was disposed but its resume
/// path exists, and a <see cref="Gone"/> agent is no longer addressable.
/// </summary>
public enum AgentStatus
{
    Running,
    Passivated,
    Gone,
}

/// <summary>A roster entry describing one addressable agent (keyed by session id).</summary>
public sealed record AgentInfo(
    string AgentId,
    string? Name,
    string? Role,
    string? ParentAgentId,
    AgentStatus Status,
    string Cwd,
    string? Model,
    string? ThinkingLevel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActiveAt);

/// <summary>Snapshot of the agents known to the messaging surface.</summary>
public sealed record AgentRoster(IReadOnlyList<AgentInfo> Agents);

/// <summary>
/// Delivery semantics of an agent-to-agent message: <see cref="Steer"/> injects
/// into the target harness immediately, <see cref="FollowUp"/> appends as a
/// follow-up on the next turn, <see cref="NextTurn"/> is delivered at the start
/// of the next turn.
/// </summary>
public enum AgentMessageDelivery
{
    Steer,
    FollowUp,
    NextTurn,
}

/// <summary>Delivery state of a message.</summary>
public enum AgentMessageStatus
{
    Queued,
    Delivered,
    Failed,
}

/// <summary>
/// An agent-to-agent message envelope. <see cref="ToAgentIds"/> holds the
/// resolved concrete recipients (never the "all"/"parent" aliases).
/// </summary>
public sealed record AgentMessage(
    string MessageId,
    string FromAgentId,
    IReadOnlyList<string> ToAgentIds,
    string Body,
    AgentMessageDelivery Delivery,
    DateTimeOffset Timestamp,
    AgentMessageStatus Status);

/// <summary>Per-recipient outcome of a routed message.</summary>
public sealed record AgentMessageRecipient(string AgentId, AgentMessageStatus Status);

/// <summary>
/// Result of routing one message. <see cref="ErrorCode"/>/<see cref="ErrorMessage"/>
/// are set when target validation rejected the message before enqueue.
/// </summary>
public sealed record AgentMessageSendResult(
    string MessageId,
    IReadOnlyList<AgentMessageRecipient> Recipients,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsError => ErrorCode is not null;
}

/// <summary>
/// Outcome of target validation: <see cref="Valid"/> recipients resolved from the
/// raw targets, or the typed rejection (<see cref="ErrorCode"/> +
/// <see cref="ErrorMessage"/>).
/// </summary>
public sealed record TargetValidationResult(
    bool Valid,
    IReadOnlyList<string> Recipients,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Typed error codes surfaced by the messaging surface.</summary>
public static class AgentMessagingErrorCodes
{
    /// <summary>Unknown, out-of-family, or policy-forbidden target.</summary>
    public const string TargetInvalid = "agent_message_target_invalid";

    /// <summary>Messaging is disabled via settings.</summary>
    public const string Disabled = "agent_messaging_disabled";

    /// <summary>Message body exceeds the configured maximum length.</summary>
    public const string BodyTooLong = "agent_message_body_too_long";

    /// <summary>No such message in the local inbox window.</summary>
    public const string MessageNotFound = "agent_message_not_found";
}

/// <summary>Operations of the model-facing <c>hub</c> tool.</summary>
public enum HubOperation
{
    List,
    Send,
    Watch,
    Steer,
}

/// <summary>Input record of the <c>hub</c> tool (schema from <c>ToolSchemas.FromType</c>).</summary>
public sealed record HubToolInput(
    HubOperation Operation,
    string? Target = null,
    string? Body = null,
    string? Delivery = null,
    bool? Watch = null,
    int? Limit = null);

/// <summary>Input record of <c>agent_message.send</c>.</summary>
public sealed record AgentMessageSendInput(string Receiver, string Body);

/// <summary>Input record of <c>agent_message.read</c>.</summary>
public sealed record AgentMessageReadInput(string? Since = null, int? Limit = null);

/// <summary>
/// Canonical JSON options for the messaging surface: web (camelCase) defaults
/// with string-encoded enums so wire payloads and the JSONL outbox are stable.
/// </summary>
internal static class AgentMessagingJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    static AgentMessagingJson()
    {
        Options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    }
}
