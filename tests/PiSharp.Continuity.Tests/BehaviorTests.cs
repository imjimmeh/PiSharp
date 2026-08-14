using PiSharp.Abstractions.Messages;
using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity.Tests;

public class HeartbeatTests
{
    private readonly FakeStateStore _store = new();
    private readonly FakeEvents _events = new();
    private readonly InMemoryKeepAliveRegistry _keepAlive = new();
    private readonly ContinuityClock _clock = new(() => new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task Tick_delivers_when_idle_with_active_goal()
    {
        var harness = new FakeHarness { Idle = true };
        var goals = new GoalService(_store, _events, _keepAlive, _clock);
        await goals.SetGoalAsync("objective", null, _ct);
        await goals.StartAsync(_ct);

        var heartbeat = new HeartbeatService(_store, _events, harness, goals, _clock, _keepAlive, requireGoal: true);
        await heartbeat.SetAsync(5, _ct);

        var delivered = await heartbeat.TickAsync(_ct);
        Assert.True(delivered);
        var sent = Assert.Single(harness.Sent);
        Assert.True(sent.TriggerTurn);
        Assert.Contains(_events.Emitted, e => e.Name == "heartbeat_tick");
    }

    [Fact]
    public async Task Tick_skips_when_requireGoal_and_no_active_goal()
    {
        var harness = new FakeHarness { Idle = true };
        var goals = new GoalService(_store, _events, _keepAlive, _clock);
        await goals.SetGoalAsync("objective", null, _ct); // idle, not active

        var heartbeat = new HeartbeatService(_store, _events, harness, goals, _clock, _keepAlive, requireGoal: true);
        await heartbeat.SetAsync(5, _ct);

        var delivered = await heartbeat.TickAsync(_ct);
        Assert.False(delivered);
        Assert.Empty(harness.Sent);
    }

    [Fact]
    public async Task Tick_queues_rather_than_interleaving_when_pending_messages()
    {
        var harness = new FakeHarness { Idle = false, Pending = true };
        var goals = new GoalService(_store, _events, _keepAlive, _clock);
        await goals.SetGoalAsync("objective", null, _ct);
        await goals.StartAsync(_ct);

        var heartbeat = new HeartbeatService(_store, _events, harness, goals, _clock, _keepAlive, requireGoal: true);
        await heartbeat.SetAsync(5, _ct);

        var delivered = await heartbeat.TickAsync(_ct);
        // Pending guard suppresses the prompt to avoid pile-up.
        Assert.False(delivered);
        Assert.Empty(harness.Sent);
    }

    [Fact]
    public async Task Disable_clears_keep_alive()
    {
        var harness = new FakeHarness();
        var goals = new GoalService(_store, _events, _keepAlive, _clock);
        var heartbeat = new HeartbeatService(_store, _events, harness, goals, _clock, _keepAlive, requireGoal: true);
        await heartbeat.SetAsync(15, _ct);
        Assert.Contains(HeartbeatService.KeepAliveReason, _keepAlive.Reasons);
        await heartbeat.DisableAsync(_ct);
        Assert.DoesNotContain(HeartbeatService.KeepAliveReason, _keepAlive.Reasons);
    }
}

public class AutonomousRunnerTests
{
    private readonly FakeStateStore _store = new();
    private readonly FakeEvents _events = new();
    private readonly InMemoryKeepAliveRegistry _keepAlive = new();
    private readonly DateTimeOffset _now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly ContinuityClock _clock = new(() => new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly CancellationToken _ct = CancellationToken.None;

    private (GoalService goals, FakeHarness harness, AutonomousRunner runner) Create(
        int maxTurns = 3, string overshootPolicy = "soft")
    {
        var harness = new FakeHarness { Idle = true };
        var goals = new GoalService(_store, _events, _keepAlive, _clock);
        var gates = new QualityGateRunner("");
        var runner = new AutonomousRunner(_store, _events, harness, goals, _clock, gates, _keepAlive,
            overshootPolicy: overshootPolicy, defaultMaxTurns: maxTurns, defaultTimeoutMinutes: 30);
        return (goals, harness, runner);
    }

    [Fact]
    public async Task Start_delivers_instruction_and_marks_running()
    {
        var (goals, harness, runner) = Create();
        await goals.SetGoalAsync("objective", null, _ct);

        var result = await runner.StartAsync(new AutonomousCommand(null, MaxTurns: 3, MaxTokens: null, TimeoutMinutes: 30, Gates: null), goals.Goal!.Objective, _ct);
        Assert.True(result.State.Running);
        Assert.Equal("objective", result.State.Instruction); // no message → goal objective
        Assert.Equal(goals.Goal!.Id, result.State.GoalId);
        var sent = Assert.Single(harness.Sent);
        Assert.True(sent.TriggerTurn);
        Assert.Contains(AutonomousRunner.KeepAliveReason, _keepAlive.Reasons);
    }

    [Fact]
    public async Task Soft_stop_ends_with_completed_when_maxTurns_reached()
    {
        var (_, harness, runner) = Create(maxTurns: 1);
        await runner.StartAsync(new AutonomousCommand("do the work", MaxTurns: 1, MaxTokens: null, TimeoutMinutes: 30, Gates: null), null, _ct);
        harness.Sent.Clear();

        // Turn 1: under max → queue a nudge.
        await runner.OnTurnEndAsync(_ct);
        Assert.Single(harness.Sent);

        // Turn 2: maxTurns reached → finish (Completed).
        await runner.OnTurnEndAsync(_ct);
        Assert.NotNull(runner.Run);
        Assert.Equal(AutonomousEndReason.Completed, runner.Run!.EndReason);
        Assert.False(runner.Run.Running);
        Assert.Contains(_events.Emitted, e => e.Name == "autonomous_ended");
        Assert.DoesNotContain(AutonomousRunner.KeepAliveReason, _keepAlive.Reasons);
    }

    [Fact]
    public async Task Budget_exhausted_ends_with_budgetExhausted()
    {
        var (_, harness, runner) = Create();
        await runner.StartAsync(new AutonomousCommand("do the work", MaxTurns: 5, MaxTokens: 100, TimeoutMinutes: 30, Gates: null), null, _ct);
        harness.Sent.Clear();

        await runner.OnAssistantMessageAsync(new AssistantMessage([], Usage: new UsageInfo(TotalTokens: 90), Timestamp: _now), _ct);
        // Still under budget → turn end queues a continuation.
        await runner.OnTurnEndAsync(_ct);
        Assert.Single(harness.Sent);

        // Second message pushes usage over 100.
        await runner.OnAssistantMessageAsync(new AssistantMessage([], Usage: new UsageInfo(TotalTokens: 20), Timestamp: _now), _ct);
        await runner.OnTurnEndAsync(_ct);
        Assert.Equal(AutonomousEndReason.BudgetExhausted, runner.Run!.EndReason);
    }

    [Fact]
    public async Task Stop_hard_aborts()
    {
        var (_, harness, runner) = Create();
        await runner.StartAsync(new AutonomousCommand("do the work", MaxTurns: 5, MaxTokens: null, TimeoutMinutes: 30, Gates: null), null, _ct);
        await runner.StopAsync(_ct);
        Assert.Equal(AutonomousEndReason.Aborted, runner.Run!.EndReason);
        Assert.False(runner.Run.Running);
    }

    [Fact]
    public async Task Gate_failure_ends_with_gateFailed()
    {
        var (_, harness, runner) = Create(maxTurns: 1);
        var failingGate = new QualityGate("g", "exit /b 1", 5, 0);
        await runner.StartAsync(new AutonomousCommand("do the work", MaxTurns: 1, MaxTokens: null, TimeoutMinutes: 30,
            Gates: new[] { failingGate }), null, _ct);
        harness.Sent.Clear();
        await runner.OnTurnEndAsync(_ct);
        await runner.OnTurnEndAsync(_ct);

        Assert.Equal(AutonomousEndReason.GateFailed, runner.Run!.EndReason);
        Assert.NotNull(runner.Run.GateResults);
        Assert.All(runner.Run.GateResults, r => Assert.False(r.Passed));
    }
}
