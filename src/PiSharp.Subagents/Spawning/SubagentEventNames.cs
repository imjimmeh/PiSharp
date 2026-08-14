using System.Text.Json;

namespace PiSharp.Subagents.Spawning;

/// <summary>
/// Event names emitted by the subagent framework. Snake-case names carry the typed payloads below;
/// the colon-form names keep existing coordination observation (PiSubagentsEventAdapter) working and
/// are mapped 1:1. The daemon wire surface (get_agents, server event envelopes) is deferred to P01.
/// </summary>
public static class SubagentEventNames
{
    // Snake-case typed surface (plan §5.7).
    public const string Created = "subagent_created";
    public const string Started = "subagent_started";
    public const string Completed = "subagent_completed";
    public const string Blocked = "subagent_blocked";

    // Colon-form compat surface observed by PiSharp.Coordination.
    public const string ColonCreated = "subagents:created";
    public const string ColonStarted = "subagents:started";
    public const string ColonCompleted = "subagents:completed";
    public const string ColonBlocked = "subagents:blocked";
}

public sealed record SubagentCreatedEvent(string Agent, string SessionId, string? ParentSessionId, int Depth);
public sealed record SubagentStartedEvent(string SessionId, string Agent, string ToolCallId);
public sealed record SubagentCompletedEvent(string SessionId, string Agent, JsonElement? StructuredResult, string Status);
public sealed record SubagentBlockedEvent(string Agent, string Reason, int Depth);

/// <summary>Colon-form payload compatible with PiSubagentsEventAdapter's mapping.</summary>
public sealed record SubagentsObservedEvent(string Id, string? Type = null, string? Description = null, string? Status = null, double? DurationMs = null);
