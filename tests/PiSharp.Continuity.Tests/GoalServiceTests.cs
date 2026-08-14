using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity.Tests;

public class GoalServiceTests : IDisposable
{
    private readonly FakeStateStore _store = new();
    private readonly FakeEvents _events = new();
    private readonly InMemoryKeepAliveRegistry _keepAlive = new();
    private readonly DateTimeOffset _now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly ContinuityClock _clock;
    private readonly GoalService _svc;
    private readonly CancellationToken _ct = CancellationToken.None;

    public GoalServiceTests()
    {
        _clock = new ContinuityClock(() => _now);
        _svc = new GoalService(_store, _events, _keepAlive, _clock);
    }
    public void Dispose() { }

    private async Task<ContinuityGoal> NewActiveGoalAsync(long? maxTokens = null)
    {
        await _svc.SetGoalAsync("finish auth refactor", maxTokens, _ct);
        await _svc.StartAsync(_ct);
        return (await _svc.GetGoalAsync(_ct)).Goal!;
    }

    [Fact]
    public async Task SetGoal_creates_idle_goal_and_persists()
    {
        var goal = (await _svc.SetGoalAsync("objective", null, _ct)).Goal!;
        Assert.Equal(ContinuityGoalStatus.Idle, goal.Status);
        Assert.Null(goal.MaxTokens);
        Assert.Same(goal, _store.Goal);
        Assert.Contains(_events.Emitted, e => e.Name == "goal_updated");
    }

    [Fact]
    public async Task Start_sets_active_with_startedAt()
    {
        await _svc.SetGoalAsync("objective", null, _ct);
        var goal = (await _svc.StartAsync(_ct)).Goal!;
        Assert.Equal(ContinuityGoalStatus.Active, goal.Status);
        Assert.Equal(_now, goal.StartedAt);
    }

    [Fact]
    public async Task Pause_resume_transitions()
    {
        await _svc.SetGoalAsync("objective", null, _ct);
        await _svc.StartAsync(_ct);
        var paused = (await _svc.PauseAsync(_ct)).Goal!;
        Assert.Equal(ContinuityGoalStatus.Paused, paused.Status);
        var resumed = (await _svc.ResumeAsync(_ct)).Goal!;
        Assert.Equal(ContinuityGoalStatus.Active, resumed.Status);
    }

    [Fact]
    public async Task Complete_sets_completedAt()
    {
        await _svc.SetGoalAsync("objective", null, _ct);
        await _svc.StartAsync(_ct);
        var goal = (await _svc.CompleteAsync(_ct)).Goal!;
        Assert.Equal(ContinuityGoalStatus.Complete, goal.Status);
        Assert.Equal(_now, goal.CompletedAt);
    }

    [Fact]
    public async Task Clear_removes_goal()
    {
        await _svc.SetGoalAsync("objective", null, _ct);
        var result = await _svc.ClearAsync(_ct);
        Assert.Null(result.Goal);
        Assert.Null(_store.Goal);
        Assert.Null((await _svc.GetGoalAsync(_ct)).Goal);
    }

    [Fact]
    public async Task Budget_zero_is_unlimited()
    {
        var goal = (await _svc.SetGoalAsync("objective", 0, _ct)).Goal!;
        Assert.Null(goal.MaxTokens);
    }

    [Fact]
    public async Task Accounting_only_while_active_and_after_startedAt()
    {
        // Idle goal — accounting ignored.
        await _svc.SetGoalAsync("objective", null, _ct);
        var (beforeStart, _) = await _svc.AccountTokensAsync(100, _now, _ct);
        Assert.Equal(0, beforeStart!.TokensUsed);

        await _svc.StartAsync(_ct);
        // Message before StartedAt — ignored.
        var (beforeTs, _) = await _svc.AccountTokensAsync(100, _now.AddMinutes(-1), _ct);
        Assert.Equal(0, beforeTs!.TokensUsed);

        // At/after StartedAt — counted.
        var (after, _) = await _svc.AccountTokensAsync(100, _now, _ct);
        Assert.Equal(100, after!.TokensUsed);
        Assert.Equal(100, _store.Goal!.TokensUsed);
    }

    [Fact]
    public async Task Reaching_budget_transitions_to_budgetLimited()
    {
        await NewActiveGoalAsync(maxTokens: 150);
        var (goal, hit) = await _svc.AccountTokensAsync(100, _now, _ct);
        Assert.False(hit);
        var (goal2, hit2) = await _svc.AccountTokensAsync(100, _now, _ct);
        Assert.True(hit2);
        Assert.Equal(ContinuityGoalStatus.BudgetLimited, goal2!.Status);
        Assert.Equal(200, goal2.TokensUsed);
        Assert.Equal(2, _events.Emitted.Count(e => e.Name == "budget_updated"));
    }

    [Fact]
    public async Task Null_usage_accounting_ignored()
    {
        await NewActiveGoalAsync();
        var (goal, _) = await _svc.AccountTokensAsync(0, _now, _ct);
        Assert.Equal(0, goal!.TokensUsed);
    }

    [Fact]
    public async Task KeepAlive_held_while_active_released_on_complete()
    {
        await _svc.SetGoalAsync("objective", null, _ct);
        Assert.DoesNotContain(GoalService.KeepAliveReason, _keepAlive.Reasons);
        await _svc.StartAsync(_ct);
        Assert.Contains(GoalService.KeepAliveReason, _keepAlive.Reasons);
        await _svc.CompleteAsync(_ct);
        Assert.DoesNotContain(GoalService.KeepAliveReason, _keepAlive.Reasons);
    }

    [Fact]
    public async Task KeepAlive_released_on_pause()
    {
        await _svc.SetGoalAsync("objective", null, _ct);
        await _svc.StartAsync(_ct);
        await _svc.PauseAsync(_ct);
        Assert.DoesNotContain(GoalService.KeepAliveReason, _keepAlive.Reasons);
    }
}
