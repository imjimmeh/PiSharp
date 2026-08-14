using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity.Tests;

/// <summary>
/// In-memory <see cref="IContinuityStateStore"/> for unit tests. Mirrors the
/// P02-style keyed persistence (goal/jobs/heartbeat/autonomous) so the
/// persistence round-trip is exercised without a file backend.
/// </summary>
internal sealed class FakeStateStore : IContinuityStateStore
{
    public ContinuityGoal? Goal;
    public List<ContinuityJob> Jobs = [];
    public HeartbeatState? Heartbeat;
    public AutonomousRunState? Autonomous;
    public int SaveGoalCount;

    public Task<ContinuityGoal?> LoadGoalAsync(CancellationToken ct) => Task.FromResult(Goal);
    public Task SaveGoalAsync(ContinuityGoal? goal, CancellationToken ct) { Goal = goal; SaveGoalCount++; return Task.CompletedTask; }
    public Task<IReadOnlyList<ContinuityJob>> LoadJobsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ContinuityJob>>(Jobs.ToList());
    public Task SaveJobsAsync(IReadOnlyList<ContinuityJob> jobs, CancellationToken ct) { Jobs = jobs.ToList(); return Task.CompletedTask; }
    public Task<HeartbeatState?> LoadHeartbeatAsync(CancellationToken ct) => Task.FromResult(Heartbeat);
    public Task SaveHeartbeatAsync(HeartbeatState heartbeat, CancellationToken ct) { Heartbeat = heartbeat; return Task.CompletedTask; }
    public Task<AutonomousRunState?> LoadAutonomousAsync(CancellationToken ct) => Task.FromResult(Autonomous);
    public Task SaveAutonomousAsync(AutonomousRunState? run, CancellationToken ct) { Autonomous = run; return Task.CompletedTask; }
    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Records continuity events emitted by the engine.</summary>
internal sealed class FakeEvents : IContinuityEvents
{
    public List<(string Name, object? Payload)> Emitted = [];
    public int BudgetUpdatedCount;

    public Task GoalUpdatedAsync(ContinuityGoal goal, CancellationToken ct) { Emitted.Add(("goal_updated", goal)); return Task.CompletedTask; }
    public Task BudgetUpdatedAsync(string? goalId, string? runId, long tokensUsed, long budgetTokens, int? remainingTurns, string reason, CancellationToken ct)
    { BudgetUpdatedCount++; Emitted.Add(("budget_updated", new { goalId, runId, tokensUsed, budgetTokens, reason })); return Task.CompletedTask; }
    public Task AutonomousEndedAsync(AutonomousRunState run, CancellationToken ct) { Emitted.Add(("autonomous_ended", run)); return Task.CompletedTask; }
    public Task ScheduledPromptAsync(string jobId, string name, string tickId, string cron, DateTimeOffset dueAt, DateTimeOffset deliveredAt, string prompt, CancellationToken ct)
    { Emitted.Add(("scheduled_prompt", new { jobId, tickId })); return Task.CompletedTask; }
    public Task HeartbeatTickAsync(string? goalId, DateTimeOffset at, CancellationToken ct) { Emitted.Add(("heartbeat_tick", new { goalId, at })); return Task.CompletedTask; }
}

/// <summary>
/// Fake harness gateway. <see cref="ThrowOnSend"/> lets tests inject a crash
/// between claim (persist) and deliver to prove claim-and-advance safety.
/// </summary>
internal sealed class FakeHarness : IHarnessGateway
{
    public List<(string Content, bool TriggerTurn)> Sent = [];
    public int AppendCount;
    public bool Idle = true;
    public bool Pending = false;
    public int AbortCount;
    public bool ThrowOnSend;

    public Task SendUserMessageAsync(string content, bool triggerTurn, CancellationToken ct)
    {
        if (ThrowOnSend) throw new InvalidOperationException("injected crash");
        Sent.Add((content, triggerTurn));
        return Task.CompletedTask;
    }
    public Task<bool> IsIdleAsync(CancellationToken ct) => Task.FromResult(Idle);
    public Task<bool> HasPendingMessagesAsync(CancellationToken ct) => Task.FromResult(Pending);
    public Task AppendAuditAsync(string customType, object data, CancellationToken ct) { AppendCount++; return Task.CompletedTask; }
    public Task AbortAsync(CancellationToken ct) { AbortCount++; return Task.CompletedTask; }
}
