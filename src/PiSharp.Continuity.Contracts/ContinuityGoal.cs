namespace PiSharp.Continuity.Contracts;

/// <summary>
/// Lifecycle status of a continuity goal. Transitions are enforced by the
/// plugin's <c>GoalService</c>; see the continuity plan §4.1.
/// </summary>
public enum ContinuityGoalStatus
{
    /// <summary>No active work; created but not started (or paused/ended).</summary>
    Idle,
    /// <summary>In progress — token accounting is active and keep-alive is held.</summary>
    Active,
    /// <summary>User-suspended — accounting is suspended, keep-alive released.</summary>
    Paused,
    /// <summary>Hit its token budget — accounting stops, keep-alive released.</summary>
    BudgetLimited,
    /// <summary>Explicitly completed via <c>/goal complete</c> or <c>update_goal</c>.</summary>
    Complete,
    /// <summary>A scheduler/delivery failure occurred.</summary>
    Error,
}

/// <summary>
/// The single per-session goal (v1: one active goal per session, mirroring
/// prime's <c>GoalState</c>). Persisted under the P23 namespace store, user
/// scope, keyed by runtime session id.
/// </summary>
public sealed record ContinuityGoal(
    string Id,                       // v7 uuid
    string Objective,
    string Progress,                 // model/user-updatable free text
    ContinuityGoalStatus Status,     // default Idle
    long? MaxTokens,                 // null = unlimited (continuity.goal.maxTokensPerGoal)
    long TokensUsed,                 // accounted from AssistantMessage.Usage.TotalTokens
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage = null);
