using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// Per-session heartbeat. On a tick, if <c>requireGoal</c> and no goal is
/// <c>active</c> the tick is skipped; if the harness is busy the re-prompt is
/// queued (rides the next run, never interleaves mid-turn); if idle with nothing
/// pending it is delivered as a fresh turn. Keeps <c>continuity:heartbeat</c>
/// while enabled and goal-satisfied.
/// </summary>
public sealed class HeartbeatService
{
    public const string KeepAliveReason = "continuity:heartbeat";

    private readonly IContinuityStateStore _store;
    private readonly IContinuityEvents _events;
    private readonly IHarnessGateway _harness;
    private readonly GoalService _goals;
    private readonly ContinuityClock _clock;
    private readonly IKeepAliveRegistry _keepAlive;
    private readonly bool _keepAliveOnHeartbeat;
    private readonly bool _requireGoal;
    private readonly string _promptTemplate;

    private HeartbeatState _state;
    private readonly object _gate = new();

    public HeartbeatState? State { get { lock (_gate) return _state; } }

    public HeartbeatService(
        IContinuityStateStore store,
        IContinuityEvents events,
        IHarnessGateway harness,
        GoalService goals,
        ContinuityClock clock,
        IKeepAliveRegistry keepAlive,
        bool requireGoal = true,
        string promptTemplate = "Heartbeat: check progress on the current goal. Continue working if there is remaining budget; otherwise report status and stop.",
        bool keepAliveOnHeartbeat = true)
    {
        _store = store;
        _events = events;
        _harness = harness;
        _goals = goals;
        _clock = clock;
        _keepAlive = keepAlive;
        _requireGoal = requireGoal;
        _promptTemplate = promptTemplate;
        _state = new HeartbeatState(false, 15);
        _keepAliveOnHeartbeat = keepAliveOnHeartbeat;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var loaded = await _store.LoadHeartbeatAsync(ct);
        lock (_gate)
        {
            _state = loaded ?? new HeartbeatState(false, 15, null);
            // Drop LastTickAt if it would make the interval look due to a stale
            // clock — recompute at the first scheduler wake.
        }
        SyncKeepAlive();
    }

    public async Task<HeartbeatState> SetAsync(int intervalMinutes, CancellationToken ct)
    {
        lock (_gate)
        {
            _state = new HeartbeatState(true, intervalMinutes > 0 ? intervalMinutes : 15, _state.LastTickAt);
        }
        await PersistAsync(ct);
        SyncKeepAlive();
        return State!;
    }

    public async Task<HeartbeatState> DisableAsync(CancellationToken ct)
    {
        lock (_gate) _state = _state with { Enabled = false };
        await PersistAsync(ct);
        SyncKeepAlive();
        return State!;
    }

    /// <summary>Returns the next UTC tick time (used by the scheduler), or null when disabled.</summary>
    public DateTimeOffset? NextTickAt(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!_state.Enabled) return null;
            if (_state.LastTickAt is not { } last) return nowUtc;
            return last.AddMinutes(_state.IntervalMinutes);
        }
    }

    /// <summary>
    /// Fires one heartbeat. Returns true when a prompt was delivered (idle
    /// trigger) or queued (busy), false when skipped (no active goal, pending
    /// messages). The scheduler guards on next-tick timing before calling.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_state.Enabled) return false;
            _state = _state with { LastTickAt = _clock.UtcNow };
        }
        await PersistAsync(ct);

        var goalActive = _goals.Goal?.Status == ContinuityGoalStatus.Active;
        if (_requireGoal && !goalActive) return false;

        var hasPending = await _harness.HasPendingMessagesAsync(ct);
        if (hasPending) return false; // never pile up

        var idle = await _harness.IsIdleAsync(ct);
        if (idle)
            await _harness.SendUserMessageAsync(_promptTemplate, triggerTurn: true, ct);
        else
            await _harness.SendUserMessageAsync(_promptTemplate, triggerTurn: false, ct);

        await _events.HeartbeatTickAsync(_goals.Goal?.Id, _clock.UtcNow, ct);
        return true;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        HeartbeatState s;
        lock (_gate) s = _state;
        await _store.SaveHeartbeatAsync(s, ct);
    }

    private void SyncKeepAlive()
    {
        if (!_keepAliveOnHeartbeat) return;
        bool enabled;
        lock (_gate) enabled = _state.Enabled;
        if (enabled) _keepAlive.Add(KeepAliveReason);
        else _keepAlive.Remove(KeepAliveReason);
    }
}
