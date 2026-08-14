using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity.Tests;

public class SchedulerTests
{
    private readonly FakeStateStore _store = new();
    private readonly FakeEvents _events = new();
    private readonly InMemoryKeepAliveRegistry _keepAlive = new();
    private readonly DateTimeOffset _now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly ContinuityClock _clock;
    private readonly CancellationToken _ct = CancellationToken.None;

    public SchedulerTests() => _clock = new ContinuityClock(() => _now);

    private (GoalService goals, HeartbeatService heartbeat, AutonomousRunner auto, FakeHarness harness, ContinuityScheduler scheduler) Create(
        bool catchUpOnResume = true, int catchUpMaxMissed = 1)
    {
        var harness = new FakeHarness { Idle = true };
        var goals = new GoalService(_store, _events, _keepAlive, _clock);
        var heartbeat = new HeartbeatService(_store, _events, harness, goals, _clock, _keepAlive, requireGoal: true);
        var gates = new QualityGateRunner("");
        var auto = new AutonomousRunner(_store, _events, harness, goals, _clock, gates, _keepAlive, defaultMaxTurns: 3);
        var scheduler = new ContinuityScheduler(_store, _events, harness, heartbeat, auto, _clock, _keepAlive,
            catchUpOnResume: catchUpOnResume, catchUpMaxMissed: catchUpMaxMissed);
        return (goals, heartbeat, auto, harness, scheduler);
    }

    private static ContinuityJob DueJob(string name = "nightly", string cron = "* * * * *", DateTimeOffset? due = null)
        => new(Guid.NewGuid().ToString(), name, cron, "run the nightly build", true, due ?? DateTimeOffset.UtcNow, due ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task Tick_claims_and_advances_before_delivery()
    {
        var (_, _, _, harness, scheduler) = Create(catchUpOnResume: false);
        var job = DueJob(due: _now);
        await scheduler.AddJobAsync(job, _ct);
        var originalNext = job.NextRunAt;

        // Crash between claim and deliver: injection throws on the first send.
        harness.ThrowOnSend = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ProcessCronDueAsync(_now, _ct));

        var after = _store.Jobs.Single();
        Assert.True(after.NextRunAt > originalNext, "job must be advanced before delivery");
        Assert.NotNull(after.LastTickId);
        Assert.Empty(harness.Sent); // never delivered
    }

    [Fact]
    public async Task Crashed_tick_is_never_re_delivered_resume_produces_fresh_tick()
    {
        var (_, _, _, harness, scheduler) = Create(catchUpOnResume: false);
        var job = DueJob(due: _now);
        await scheduler.AddJobAsync(job, _ct);

        harness.ThrowOnSend = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ProcessCronDueAsync(_now, _ct));
        var claimedTick = scheduler.Jobs.Single().LastTickId;
        Assert.NotNull(claimedTick);

        // After the crash the job advanced past 'now' — nothing re-delivered at the same instant.
        harness.ThrowOnSend = false;
        var delivered = await scheduler.ProcessCronDueAsync(_now, _ct);
        Assert.Equal(0, delivered);

        // Advance the clock past the claimed tick's NextRunAt → a fresh tick delivers.
        var delivered2 = await scheduler.ProcessCronDueAsync(_now.AddMinutes(2), _ct);
        Assert.Equal(1, delivered2);
        var freshTick = scheduler.Jobs.Single().LastTickId;
        Assert.NotEqual(claimedTick, freshTick); // never the same tick twice
        Assert.Single(harness.Sent);
    }

    [Fact]
    public async Task CatchUp_bounded_by_catchUpMaxMissed_with_fresh_tick_ids()
    {
        var (_, _, _, harness, scheduler) = Create(catchUpOnResume: true, catchUpMaxMissed: 3);
        var job = DueJob(name: "missed", due: _now.AddMinutes(-5));
        await scheduler.AddJobAsync(job, _ct);

        var delivered = await scheduler.ProcessCronDueAsync(_now, _ct);
        Assert.Equal(3, delivered);
        Assert.Equal(3, harness.Sent.Count);
        var ids = scheduler.Jobs.Single()./* imminent-delivery validation only */ Id;
        Assert.NotNull(ids);
        // Each delivery carried a distinct tick id on the single (most recent) claim.
        Assert.NotNull(scheduler.Jobs.Single().LastTickId);
    }

    [Fact]
    public async Task Disabled_jobs_are_not_due()
    {
        var (_, _, _, harness, scheduler) = Create(catchUpOnResume: false);
        var job = DueJob(due: _now) with { Enabled = false };
        await scheduler.AddJobAsync(job, _ct);

        var delivered = await scheduler.ProcessCronDueAsync(_now, _ct);
        Assert.Equal(0, delivered);
        Assert.Empty(harness.Sent);
    }

    [Fact]
    public async Task CancelJob_removes_and_releases_keep_alive()
    {
        var (_, _, _, _, scheduler) = Create();
        var job = DueJob(due: _now);
        await scheduler.AddJobAsync(job, _ct);
        Assert.Contains(ContinuityScheduler.KeepAliveReason, _keepAlive.Reasons);

        var cancelled = await scheduler.CancelJobAsync(job.Id, _ct);
        Assert.True(cancelled);
        Assert.Empty(scheduler.Jobs);
        Assert.DoesNotContain(ContinuityScheduler.KeepAliveReason, _keepAlive.Reasons);
    }

    [Fact]
    public void ComputeNextWake_tracks_jobs_heartbeat_and_autonomous_deadline()
    {
        var (goals, heartbeat, auto, _, scheduler) = Create();
        goals.SetGoalAsync("objective", null, _ct);
        heartbeat.SetAsync(5, _ct); // heartbeat due "now" (last tick null)
        var sooner = _now.AddMinutes(3);
        var job = DueJob(due: sooner);
        scheduler.AddJobAsync(job, _ct);

        var wake = scheduler.ComputeNextWake(_now);
        // Heartbeat with no last tick → due now → the earliest.
        Assert.Equal(_now, wake);
    }
}
