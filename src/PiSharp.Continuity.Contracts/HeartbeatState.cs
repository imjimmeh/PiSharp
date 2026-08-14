namespace PiSharp.Continuity.Contracts;

/// <summary>
/// Per-session heartbeat state. On each tick, if <c>requireGoal</c> and no
/// goal is <c>active</c> the tick is skipped; otherwise a re-prompt is
/// delivered (queued rather than interleaved if the harness is busy).
/// </summary>
public sealed record HeartbeatState(
    bool Enabled,
    int IntervalMinutes,
    DateTimeOffset? LastTickAt = null);
