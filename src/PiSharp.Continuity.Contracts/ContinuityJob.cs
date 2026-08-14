namespace PiSharp.Continuity.Contracts;

/// <summary>
/// A per-session cron job. Ticks are claim-and-advance: <c>NextRunAt</c> is
/// advanced and a fresh <c>TickId</c> generated and persisted *before* the
/// prompt is delivered, so a crash never replays an already-claimed tick
/// (delivery is at-most-once per <c>LastTickId</c>).
/// </summary>
public sealed record ContinuityJob(
    string Id,            // v7 uuid
    string Name,          // display name, required
    string Cron,          // 5-field or @hourly/@daily/@weekly
    string Prompt,        // message delivered at tick
    bool Enabled,         // default true
    DateTimeOffset CreatedAt,
    DateTimeOffset NextRunAt,   // UTC; claim-and-advance target
    DateTimeOffset? LastRunAt = null,
    string? LastTickId = null); // last *claimed* tick (at-most-once per tick)
