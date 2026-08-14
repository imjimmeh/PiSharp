using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// Owns the single per-session goal and its status transitions (continuity
/// plan §4.1). Also owns the <c>continuity:goal</c> keep-alive reason: it is
/// held while the goal is <c>active</c> or <c>budget_limited</c> (when
/// keep-alive-on-active-goal is enabled) and released on <c>idle</c>,
/// <c>paused</c>, <c>complete</c>, or <c>error</c>.
/// </summary>
public sealed class GoalService
{
    private readonly IContinuityStateStore _store;
    private readonly IContinuityEvents _events;
    private readonly IKeepAliveRegistry _keepAlive;
    private readonly ContinuityClock _clock;
    private readonly bool _requireExplicitStart;
    private readonly bool _keepAliveOnActiveGoal;

    public const string KeepAliveReason = "continuity:goal";

    private ContinuityGoal? _goal;

    public ContinuityGoal? Goal { get { lock (_gate) return _goal; } }
    private readonly object _gate = new();

    public GoalService(
        IContinuityStateStore store,
        IContinuityEvents events,
        IKeepAliveRegistry keepAlive,
        ContinuityClock clock,
        bool requireExplicitStart = false,
        bool keepAliveOnActiveGoal = true)
    {
        _store = store;
        _events = events;
        _keepAlive = keepAlive;
        _clock = clock;
        _requireExplicitStart = requireExplicitStart;
        _keepAliveOnActiveGoal = keepAliveOnActiveGoal;
    }

    /// <summary>Loads persisted state at plugin start (create_session re-arm).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        ContinuityGoal? goal;
        lock (_gate) { }
        goal = await _store.LoadGoalAsync(ct);
        lock (_gate) _goal = goal;
        SyncKeepAliveLocked(goal);
    }

    public async Task<ContinuityGoalResult> SetGoalAsync(string objective, long? maxTokens, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var goal = new ContinuityGoal(
            Id: Guid.NewGuid().ToString(),
            Objective: objective,
            Progress: string.Empty,
            Status: ContinuityGoalStatus.Idle,
            MaxTokens: NormalizeBudget(maxTokens),
            TokensUsed: 0,
            CreatedAt: now,
            StartedAt: null,
            UpdatedAt: now,
            CompletedAt: null);
        lock (_gate) _goal = goal;
        await PersistGoalAsync(ct);
        await EmitGoalAsync(goal, ct);
        return new ContinuityGoalResult(goal);
    }

    public async Task<ContinuityGoalResult> GetGoalAsync(CancellationToken ct)
    {
        lock (_gate) return new ContinuityGoalResult(_goal);
    }

    public Task<ContinuityGoalResult> StartAsync(CancellationToken ct) => TransitionAsync(ContinuityGoalStatus.Active, starting: true, ct);
    public Task<ContinuityGoalResult> PauseAsync(CancellationToken ct) => TransitionAsync(ContinuityGoalStatus.Paused, starting: false, ct);
    public Task<ContinuityGoalResult> ResumeAsync(CancellationToken ct) => TransitionAsync(ContinuityGoalStatus.Active, starting: false, ct);

    public async Task<ContinuityGoalResult> CompleteAsync(CancellationToken ct)
    {
        ContinuityGoal? goal;
        lock (_gate) goal = _goal;
        if (goal is null) return new ContinuityGoalResult(null);

        var now = _clock.UtcNow;
        goal = goal with { Status = ContinuityGoalStatus.Complete, CompletedAt = now, UpdatedAt = now };
        lock (_gate) _goal = goal;
        await PersistGoalAsync(ct);
        await EmitGoalAsync(goal, ct);
        return new ContinuityGoalResult(goal);
    }

    public async Task<ContinuityGoalResult> ClearAsync(CancellationToken ct)
    {
        ContinuityGoal? removed;
        lock (_gate) { removed = _goal; _goal = null; }
        await _store.SaveGoalAsync(null, ct);
        SyncKeepAliveLocked(null);
        if (removed is not null)
        {
            await _events.GoalUpdatedAsync(new ContinuityGoal(
                removed.Id, removed.Objective, removed.Progress, ContinuityGoalStatus.Idle,
                removed.MaxTokens, removed.TokensUsed, removed.CreatedAt, removed.StartedAt,
                removed.UpdatedAt, removed.CompletedAt, removed.ErrorMessage), ct);
        }
        return new ContinuityGoalResult(null);
    }

    public async Task<ContinuityGoalResult> SetProgressAsync(string progress, CancellationToken ct)
    {
        ContinuityGoal? goal;
        lock (_gate) goal = _goal;
        if (goal is null) return new ContinuityGoalResult(null);
        goal = goal with { Progress = progress, UpdatedAt = _clock.UtcNow };
        lock (_gate) _goal = goal;
        await PersistGoalAsync(ct);
        await EmitGoalAsync(goal, ct);
        return new ContinuityGoalResult(goal);
    }

    public async Task<ContinuityGoalResult> SetBudgetAsync(long tokenBudget, CancellationToken ct)
    {
        ContinuityGoal? goal;
        lock (_gate) goal = _goal;
        if (goal is null) return new ContinuityGoalResult(null);
        goal = goal with { MaxTokens = NormalizeBudget(tokenBudget), UpdatedAt = _clock.UtcNow };
        lock (_gate) _goal = goal;
        await PersistGoalAsync(ct);
        await EmitGoalAsync(goal, ct);
        return new ContinuityGoalResult(goal);
    }

    /// <summary>
    /// Account a completed assistant message's tokens toward the goal while it
    /// is <c>active</c> and the message is not older than <c>StartedAt</c>.
    /// Crossing <c>MaxTokens</c> (when set) transitions to
    /// <c>budget_limited</c> automatically. Returns true when a transition to
    /// <c>budget_limited</c> occurred (the autonomous runner uses this to halt).
    /// </summary>
    public async Task<(ContinuityGoal? goal, bool hitBudget)> AccountTokensAsync(int tokens, DateTimeOffset messageTimestamp, CancellationToken ct)
    {
        ContinuityGoal? goal;
        lock (_gate) goal = _goal;
        if (goal is null) return (null, false);
        if (goal.Status != ContinuityGoalStatus.Active) return (goal, false);
        if (goal.StartedAt is null || messageTimestamp < goal.StartedAt.Value) return (goal, false);
        if (tokens <= 0) return (goal, false);

        var hitBudget = false;
        var newTokens = goal.TokensUsed + tokens;
        var now = _clock.UtcNow;
        if (goal.MaxTokens is { } max && newTokens >= max)
        {
            goal = goal with { Status = ContinuityGoalStatus.BudgetLimited, TokensUsed = newTokens, UpdatedAt = now };
            hitBudget = true;
        }
        else
        {
            goal = goal with { TokensUsed = newTokens, UpdatedAt = now };
        }
        lock (_gate) _goal = goal;
        await PersistGoalAsync(ct);
        await EmitGoalAsync(goal, ct);
        await EmitBudgetAsync(goal, null, "goal", ct);
        return (goal, hitBudget);
    }

    /// <summary>Reports the token budget line used by the autonomous runner.</summary>
    public (long used, long? budget) BudgetSnapshot()
    {
        lock (_gate)
        {
            if (_goal is null) return (0, null);
            return (_goal.TokensUsed, _goal.MaxTokens);
        }
    }

    private async Task<ContinuityGoalResult> TransitionAsync(ContinuityGoalStatus target, bool starting, CancellationToken ct)
    {
        ContinuityGoal? goal;
        lock (_gate) goal = _goal;
        if (goal is null) return new ContinuityGoalResult(null);

        var now = _clock.UtcNow;
        switch (target)
        {
            case ContinuityGoalStatus.Active:
                goal = goal with
                {
                    Status = ContinuityGoalStatus.Active,
                    StartedAt = starting || goal.StartedAt is null ? now : goal.StartedAt,
                    UpdatedAt = now,
                    ErrorMessage = null,
                };
                break;
            case ContinuityGoalStatus.Paused:
                goal = goal with { Status = ContinuityGoalStatus.Paused, UpdatedAt = now };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
        lock (_gate) _goal = goal;
        await PersistGoalAsync(ct);
        await EmitGoalAsync(goal, ct);
        return new ContinuityGoalResult(goal);
    }

    /// <summary>
    /// Called after each accounted message (from <see cref="BudgetAccountant"/>)
    /// when no status transition occurred — emits the budget line only.
    /// </summary>
    public async Task EmitBudgetOnlyAsync(string? runId, string reason, CancellationToken ct)
    {
        ContinuityGoal? goal;
        lock (_gate) goal = _goal;
        if (goal is null) return;
        await EmitBudgetAsync(goal, runId, reason, ct);
    }

    private static long? NormalizeBudget(long? maxTokens) => maxTokens is null or <= 0 ? null : maxTokens;

    private async Task PersistGoalAsync(CancellationToken ct) => await _store.SaveGoalAsync(_goal, ct);

    private void SyncKeepAliveLocked(ContinuityGoal? goal)
    {
        if (!_keepAliveOnActiveGoal) return;
        var holds = goal is not null
            && (goal.Status == ContinuityGoalStatus.Active || goal.Status == ContinuityGoalStatus.BudgetLimited);
        if (holds) _keepAlive.Add(KeepAliveReason);
        else _keepAlive.Remove(KeepAliveReason);
    }

    private async Task EmitGoalAsync(ContinuityGoal goal, CancellationToken ct)
    {
        // Snapshot for transmission; keep-alive synced under the lock.
        lock (_gate) SyncKeepAliveLocked(_goal);
        await _events.GoalUpdatedAsync(goal, ct);
    }

    private async Task EmitBudgetAsync(ContinuityGoal goal, string? runId, string reason, CancellationToken ct)
    {
        var remainingTurns = null as int?;
        await _events.BudgetUpdatedAsync(
            goal.Id, runId, goal.TokensUsed, goal.MaxTokens ?? 0, remainingTurns, reason, ct);
    }
}

/// <summary>
/// Minimal keep-alive registry so the plugin logic is testable without the
/// daemon's <c>ISessionKeepAlive</c> core surface (plan C1). The extension
/// adapts this to the daemon registry when present.
/// </summary>
public interface IKeepAliveRegistry
{
    void Add(string reason);
    void Remove(string reason);
    IReadOnlySet<string> Reasons { get; }
}

/// <summary>In-memory keep-alive set (used by unit tests and as the default).</summary>
public sealed class InMemoryKeepAliveRegistry : IKeepAliveRegistry
{
    private readonly HashSet<string> _reasons = new(StringComparer.Ordinal);
    public IReadOnlySet<string> Reasons { get { lock (_gate) return new HashSet<string>(_reasons, StringComparer.Ordinal); } }
    private readonly object _gate = new();
    public void Add(string reason) { lock (_gate) _reasons.Add(reason); }
    public void Remove(string reason) { lock (_gate) _reasons.Remove(reason); }
}
