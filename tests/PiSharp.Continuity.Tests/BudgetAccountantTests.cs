using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity.Tests;

public class BudgetAccountantTests
{
    private readonly FakeStateStore _store = new();
    private readonly FakeEvents _events = new();
    private readonly InMemoryKeepAliveRegistry _keepAlive = new();
    private readonly DateTimeOffset _now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken _ct = CancellationToken.None;

    private (GoalService svc, BudgetAccountant acc) Create()
    {
        var clock = new ContinuityClock(() => _now);
        var goals = new GoalService(_store, _events, _keepAlive, clock);
        return (goals, new BudgetAccountant(goals, _events, clock));
    }

    private static AgentEvent.MessageEnd MessageEnd(int tokens, DateTimeOffset ts)
        => new(new AssistantMessage([], Usage: new UsageInfo(TotalTokens: tokens), Timestamp: ts));

    [Fact]
    public async Task MessageEnd_accounts_tokens_when_goal_active()
    {
        var (goals, acc) = Create();
        await goals.SetGoalAsync("objective", null, _ct);
        await goals.StartAsync(_ct);

        await acc.OnMessageEndAsync(MessageEnd(50, _now), _ct);
        Assert.Equal(50, (await goals.GetGoalAsync(_ct)).Goal!.TokensUsed);
        Assert.True(_events.BudgetUpdatedCount >= 1);
    }

    [Fact]
    public async Task Null_usage_message_is_ignored()
    {
        var (goals, acc) = Create();
        await goals.SetGoalAsync("objective", null, _ct);
        await goals.StartAsync(_ct);

        // User message has no Usage.
        await acc.OnMessageEndAsync(new AgentEvent.MessageEnd(new UserMessage([new TextContent("hi")], _now)), _ct);
        Assert.Equal(0, (await goals.GetGoalAsync(_ct)).Goal!.TokensUsed);
        Assert.Equal(0, _events.BudgetUpdatedCount);
    }

    [Fact]
    public async Task Zero_total_tokens_is_ignored()
    {
        var (goals, acc) = Create();
        await goals.SetGoalAsync("objective", null, _ct);
        await goals.StartAsync(_ct);

        await acc.OnMessageEndAsync(MessageEnd(0, _now), _ct);
        Assert.Equal(0, (await goals.GetGoalAsync(_ct)).Goal!.TokensUsed);
        Assert.Equal(0, _events.BudgetUpdatedCount);
    }

    [Fact]
    public async Task Crossing_budget_limits_and_does_not_emit_budget_line()
    {
        var (goals, acc) = Create();
        await goals.SetGoalAsync("objective", 60, _ct);
        await goals.StartAsync(_ct);

        await acc.OnMessageEndAsync(MessageEnd(100, _now), _ct);
        var goal = (await goals.GetGoalAsync(_ct)).Goal!;
        Assert.Equal(ContinuityGoalStatus.BudgetLimited, goal.Status);
        Assert.Equal(100, goal.TokensUsed);
    }
}
