using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// Session-scoped persistence over the P23 namespace store. The state-store
/// adapter (<see cref="ExtensionStateStoreAdapter"/>) wraps
/// <c>IExtensionApi.State</c>; unit tests use an in-memory fake so the
/// persistence round-trip is exercised without a file backend.
/// </summary>
public interface IContinuityStateStore
{
    Task<ContinuityGoal?> LoadGoalAsync(CancellationToken ct);
    Task SaveGoalAsync(ContinuityGoal? goal, CancellationToken ct);
    Task<IReadOnlyList<ContinuityJob>> LoadJobsAsync(CancellationToken ct);
    Task SaveJobsAsync(IReadOnlyList<ContinuityJob> jobs, CancellationToken ct);
    Task<HeartbeatState?> LoadHeartbeatAsync(CancellationToken ct);
    Task SaveHeartbeatAsync(HeartbeatState heartbeat, CancellationToken ct);
    Task<AutonomousRunState?> LoadAutonomousAsync(CancellationToken ct);
    Task SaveAutonomousAsync(AutonomousRunState? run, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// Emits the client-visible continuity events over the custom-event lane
/// (<c>goal_updated</c>, <c>budget_updated</c>, <c>autonomous_ended</c>,
/// <c>scheduled_prompt</c>, <c>heartbeat_tick</c>).
/// </summary>
public interface IContinuityEvents
{
    Task GoalUpdatedAsync(ContinuityGoal goal, CancellationToken ct);
    Task BudgetUpdatedAsync(string? goalId, string? runId, long tokensUsed, long budgetTokens, int? remainingTurns, string reason, CancellationToken ct);
    Task AutonomousEndedAsync(AutonomousRunState run, CancellationToken ct);
    Task ScheduledPromptAsync(string jobId, string name, string tickId, string cron, DateTimeOffset dueAt, DateTimeOffset deliveredAt, string prompt, CancellationToken ct);
    Task HeartbeatTickAsync(string? goalId, DateTimeOffset at, CancellationToken ct);
}

/// <summary>
/// The harness surface the continuity scheduler and runners need. Abstracted
/// from <c>IExtensionApi</c> so unit tests use tiny fakes.
/// </summary>
public interface IHarnessGateway
{
    Task SendUserMessageAsync(string content, bool triggerTurn, CancellationToken ct);
    Task<bool> IsIdleAsync(CancellationToken ct);
    Task<bool> HasPendingMessagesAsync(CancellationToken ct);
    Task AppendAuditAsync(string customType, object data, CancellationToken ct);
    Task AbortAsync(CancellationToken ct);
}
