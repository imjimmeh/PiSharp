using System.Collections.Concurrent;
using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// The single background loop per plugin instance that computes the earliest
/// due of {cron jobs, heartbeat, autonomous deadline} and wakes on it. Ticks
/// are claim-and-advance (plan §3.5): the job's <c>NextRunAt</c> is advanced
/// and a fresh <c>TickId</c> generated and persisted *before* the prompt is
/// delivered, so a crash never replays an already-claimed tick. Catch-up on
/// resume uses fresh tick ids bounded by <c>cron.catchUpMaxMissed</c>.
/// </summary>
public sealed class ContinuityScheduler : IDisposable
{
    public const string KeepAliveReason = "continuity:cron";

    private readonly IContinuityStateStore _store;
    private readonly IContinuityEvents _events;
    private readonly IHarnessGateway _harness;
    private readonly HeartbeatService _heartbeat;
    private readonly AutonomousRunner _autonomous;
    private readonly ContinuityClock _clock;
    private readonly IKeepAliveRegistry _keepAlive;

    private readonly bool _keepAliveOnArmedCron;
    private readonly int _sweepResolutionSeconds;
    private readonly bool _timezoneUtc;
    private readonly bool _catchUpOnResume;
    private readonly int _catchUpMaxMissed;

    private readonly ConcurrentDictionary<string, CronSchedule> _cronCache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private List<ContinuityJob> _jobs = [];
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public ContinuityScheduler(
        IContinuityStateStore store,
        IContinuityEvents events,
        IHarnessGateway harness,
        HeartbeatService heartbeat,
        AutonomousRunner autonomous,
        ContinuityClock clock,
        IKeepAliveRegistry keepAlive,
        bool keepAliveOnArmedCron = true,
        int sweepResolutionSeconds = 15,
        bool timezoneUtc = false,
        bool catchUpOnResume = true,
        int catchUpMaxMissed = 1)
    {
        _store = store;
        _events = events;
        _harness = harness;
        _heartbeat = heartbeat;
        _autonomous = autonomous;
        _clock = clock;
        _keepAlive = keepAlive;
        _keepAliveOnArmedCron = keepAliveOnArmedCron;
        _sweepResolutionSeconds = Math.Max(1, sweepResolutionSeconds);
        _timezoneUtc = timezoneUtc;
        _catchUpOnResume = catchUpOnResume;
        _catchUpMaxMissed = Math.Max(1, catchUpMaxMissed);
    }

    public IReadOnlyList<ContinuityJob> Jobs { get { lock (_gate) return _jobs.ToArray(); } }

    public async Task LoadAsync(CancellationToken ct)
    {
        var jobs = await _store.LoadJobsAsync(ct);
        lock (_gate) _jobs = jobs.ToList();
        SyncJobKeepAlive();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loopTask is not null) return;
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate) { cts = _loopCts; task = _loopTask; _loopCts = null; _loopTask = null; }
        cts?.Cancel();
        try { task?.GetAwaiter().GetResult(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = _clock.UtcNow;
                var delay = TimeSpan.FromSeconds(_sweepResolutionSeconds);
                if (ComputeNextWake(now) is { } target)
                {
                    var d = target - now;
                    if (d > TimeSpan.Zero && d < delay) delay = d;
                }
                await Task.Delay(delay, ct);
                _ = ProcessDueAsync(_clock.UtcNow, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep the loop alive despite transient failures.
            }
        }
    }

    /// <summary>Earliest of next heartbeat tick, next enabled job run, and the running autonomous deadline.</summary>
    public DateTimeOffset? ComputeNextWake(DateTimeOffset nowUtc)
    {
        DateTimeOffset? next = null;
        if (_heartbeat.NextTickAt(nowUtc) is { } hb)
            next = Min(next, hb);
        foreach (var job in Jobs.Where(j => j.Enabled))
            next = Min(next, job.NextRunAt);
        if (_autonomous.Run is { Running: true, Deadline: var dl })
            next = Min(next, dl);
        return next;
    }

    public async Task AddJobAsync(ContinuityJob job, CancellationToken ct)
    {
        // Validate the cron expression eagerly so bad schedules fail at add time.
        _ = Parse(job.Cron);
        lock (_gate) { _jobs.RemoveAll(j => j.Id == job.Id); _jobs.Add(job); }
        await _store.SaveJobsAsync(Jobs, ct);
        SyncJobKeepAlive();
    }

    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
    {
        ContinuityJob? removed;
        lock (_gate)
        {
            removed = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (removed is not null) _jobs.Remove(removed);
        }
        if (removed is null) return false;
        await _store.SaveJobsAsync(Jobs, ct);
        SyncJobKeepAlive();
        return true;
    }

    public async Task<bool> SetJobEnabledAsync(string jobId, bool enabled, CancellationToken ct)
    {
        ContinuityJob? updated = null;
        lock (_gate)
        {
            var idx = _jobs.FindIndex(j => j.Id == jobId);
            if (idx < 0) return false;
            _jobs[idx] = _jobs[idx] with { Enabled = enabled };
            updated = _jobs[idx];
        }
        await _store.SaveJobsAsync(Jobs, ct);
        SyncJobKeepAlive();
        return true;
    }

    public async Task<int> ProcessDueAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var delivered = await ProcessCronDueAsync(nowUtc, ct);

        if (_heartbeat.NextTickAt(nowUtc) is { } hb && hb <= nowUtc)
            _ = await _heartbeat.TickAsync(ct);

        if (_autonomous.Run is { Running: true, Deadline: var dl } && dl <= nowUtc)
            await _autonomous.OnTurnEndAsync(ct);

        return delivered;
    }

    /// <summary>Claim-and-advance delivery of all due jobs. Returns the number of prompts delivered.</summary>
    public async Task<int> ProcessCronDueAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var due = Jobs.Where(j => j.Enabled && j.NextRunAt <= nowUtc).ToList();
        var delivered = 0;
        foreach (var job in due)
            delivered += await ClaimAndDeliverAsync(job, nowUtc, ct);
        return delivered;
    }

    private async Task<int> ClaimAndDeliverAsync(ContinuityJob job, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var schedule = Parse(job.Cron);
        var dueAt = job.NextRunAt;
        var advanced = job.NextRunAt;
        var catchUp = _catchUpOnResume && job.NextRunAt < nowUtc;
        var maxRuns = catchUp ? _catchUpMaxMissed : 1;
        var fired = 0;

        while (advanced <= nowUtc && fired < maxRuns)
        {
            var tickId = Guid.NewGuid().ToString();
            // CLAIM — advance NextRunAt and persist the fresh tick BEFORE delivery.
            advanced = NextInTimeZone(schedule, advanced);
            var claimed = job with { NextRunAt = advanced, LastTickId = tickId, LastRunAt = nowUtc };
            lock (_gate)
            {
                var idx = _jobs.FindIndex(j => j.Id == claimed.Id);
                if (idx >= 0) _jobs[idx] = claimed; else _jobs.Add(claimed);
            }
            await _store.SaveJobsAsync(Jobs, ct);

            // DELIVER — a crash between claim and delivery never replays this tick.
            await _harness.SendUserMessageAsync(job.Prompt, triggerTurn: true, ct);
            await _harness.AppendAuditAsync("scheduled_prompt", new { jobId = job.Id, tickId, dueAt }, ct);
            await _events.ScheduledPromptAsync(job.Id, job.Name, tickId, job.Cron, dueAt, nowUtc, job.Prompt, ct);

            fired++;
        }
        SyncJobKeepAlive();
        return fired;
    }

    private DateTimeOffset NextInTimeZone(CronSchedule schedule, DateTimeOffset afterUtc)
    {
        if (_timezoneUtc) return schedule.Next(afterUtc);
        var local = afterUtc.ToLocalTime();
        return schedule.Next(local).ToUniversalTime();
    }

    private CronSchedule Parse(string cron)
        => _cronCache.GetOrAdd(cron, static c => new CronSchedule(c));

    private void SyncJobKeepAlive()
    {
        if (!_keepAliveOnArmedCron) return;
        var hasArmed = Jobs.Any(j => j.Enabled);
        if (hasArmed) _keepAlive.Add(KeepAliveReason);
        else _keepAlive.Remove(KeepAliveReason);
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset b)
        => a is null || b < a.Value ? b : a;
}
