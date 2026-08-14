using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Streaming;
using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class RulesEngineTests
{
    private static RulesEngine Engine(
        IReadOnlyList<Rule> rules,
        RulesOptions? options = null,
        string? ruleSourceName = null)
    {
        var engine = new RulesEngine(_ => Task.FromResult(rules), options ?? new RulesOptions());
        return engine;
    }

    private static StreamDeltaContext Delta(string textDelta, AssistantMessageEvent? matchOn = null)
    {
        var partial = AgentMessages.Assistant(string.Empty);
        if (textDelta is not null)
        {
            return new StreamDeltaContext(
                new AssistantMessageEvent.TextDelta(partial, 0, textDelta),
                partial,
                new AgentContext("sys", [AgentMessages.User("u")]));
        }
        return new StreamDeltaContext(
            new AssistantMessageEvent.ThinkingDelta(partial, 0, "thinking"),
            partial,
            new AgentContext("sys", [AgentMessages.User("u")]));
    }

    [Fact]
    public async Task Discovery_OrdersByPriorityDescendingAndDedupsFirstWins()
    {
        var providerARules = new[]
        {
            new Rule("shared", "A high", Priority: 10, ApplyMode: RuleApplyMode.Always),
            new Rule("a-only", "A only", Priority: 1, ApplyMode: RuleApplyMode.Always),
        };
        var providerBRules = new[]
        {
            new Rule("shared", "B lower", Priority: 5, ApplyMode: RuleApplyMode.Always),
        };
        var engine = new RulesEngine(_ => Task.FromResult<IReadOnlyList<Rule>>(
            providerARules.Concat(providerBRules).ToArray()), new RulesOptions());

        var rules = await engine.DiscoverAsync();

        Assert.Equal(2, rules.Count);
        Assert.Equal("shared", rules[0].Name);      // higher priority wins dedup
        Assert.Equal("A high", rules[0].Content);
        Assert.Equal("a-only", rules[1].Name);      // then lower priority
    }

    [Fact]
    public async Task Discovery_TieOnNameKeepsFirstRegistered()
    {
        var rules = new[]
        {
            new Rule("dup", "from A", ApplyMode: RuleApplyMode.Always),
            new Rule("dup", "from B", ApplyMode: RuleApplyMode.Always),
        };
        var engine = new RulesEngine(_ => Task.FromResult<IReadOnlyList<Rule>>(rules), new RulesOptions());

        var result = await engine.DiscoverAsync();

        var rule = Assert.Single(result);
        Assert.Equal("from A", rule.Content);
    }

    [Fact]
    public async Task GetRulesAsync_DiscoversOnFirstUse()
    {
        var count = 0;
        var engine = new RulesEngine(_ =>
        {
            count++;
            return Task.FromResult<IReadOnlyList<Rule>>(
                [new Rule("r", "c", ApplyMode: RuleApplyMode.Always)]);
        }, new RulesOptions());

        var rules = await engine.GetRulesAsync();

        Assert.Single(rules);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Intercept_MatchesAccumulatedVisibleTextAcrossDeltasOnce()
    {
        var rule = new Rule("no-todo", "Do not add a todo list.", TriggerPattern: "(?i)todo list");
        var engine = Engine([rule]);

        var first = await engine.InterceptDeltaAsync(Delta("remember the "));
        var second = await engine.InterceptDeltaAsync(Delta("todo "));
        var third = await engine.InterceptDeltaAsync(Delta("list"));

        Assert.Null(first);
        Assert.Null(second);

        var fired = Assert.IsType<StreamDeltaDecision>(third);
        Assert.Equal(StreamDeltaAction.Retry, fired.Action);
        Assert.Equal("Do not add a todo list.", fired.SystemReminder);
        Assert.Equal("rule:no-todo:(?i)todo list", fired.Reason);
    }

    [Fact]
    public async Task Intercept_ThinkingDeltasAreIgnoredForMatching()
    {
        var rule = new Rule("no-todo", "x", TriggerPattern: "todo list");
        var engine = Engine([rule]);

        // Thinking delta contains the trigger text but must not match (visible text only).
        var thought = await engine.InterceptDeltaAsync(
            new StreamDeltaContext(
                new AssistantMessageEvent.ThinkingDelta(AgentMessages.Assistant(""), 0, "todo list thinking"),
                AgentMessages.Assistant(""),
                new AgentContext("sys", [AgentMessages.User("u")])));
        Assert.Null(thought);

        // Visible text so far excludes thinking, so still no match.
        var text = await engine.InterceptDeltaAsync(Delta("hello"));
        Assert.Null(text);
    }

    [Fact]
    public async Task Intercept_NoMatchReturnsNull()
    {
        var rule = new Rule("no-todo", "x", TriggerPattern: "unrelated");
        var engine = Engine([rule]);

        var result = await engine.InterceptDeltaAsync(Delta("plain text"));

        Assert.Null(result);
    }

    [Fact]
    public async Task Intercept_AlwaysRulesAreNotStreamMatched()
    {
        var always = new Rule("always", "always body", ApplyMode: RuleApplyMode.Always);
        var engine = Engine([always]);

        var result = await engine.InterceptDeltaAsync(Delta("any visible text"));

        Assert.Null(result);
    }

    [Fact]
    public async Task Intercept_CatastrophicPatternReturnsNoMatch_NotThrow()
    {
        var rule = new Rule("pathological", "x", TriggerPattern: "(a+)+$");
        var engine = Engine([rule]);

        // Assert does not throw: a pathological pattern must time out to no-match, never throw.
        var result = await engine.InterceptDeltaAsync(Delta(new string('a', 24) + "!"));

        Assert.Null(result);
    }

    [Fact]
    public async Task PrepareMessages_AppendsAlwaysRulesNearEnd_ProjectClosestToTurn()
    {
        var rules = new[]
        {
            new Rule("stream-rule", "stream body", TriggerPattern: "x"),                       // not injected
            new Rule("always-file", "file body", ApplyMode: RuleApplyMode.Always),             // injected
            new Rule("RULES", "user sticky", Path: "u/RULES.md", Priority: 1000, ApplyMode: RuleApplyMode.Always),
            new Rule("RULES@project", "project sticky", Path: "p/RULES.md", Priority: 1000, ApplyMode: RuleApplyMode.Always),
        };
        var engine = Engine(rules);

        var original = new List<AgentMessage> { AgentMessages.User("turn input") };
        var prepared = await engine.PrepareMessagesAsync(original, new AgentContext("sys", original));

        var last = Assert.IsType<UserMessage>(prepared[^1]);
        var lastText = Assert.IsType<TextContent>(last.Content[0]).Text;
        Assert.Contains("project sticky", lastText);

        var userMsg = prepared.OfType<UserMessage>().ElementAt(prepared.Count - 2);
        Assert.Contains("user sticky", Assert.IsType<TextContent>(userMsg.Content[0]).Text);

        // File always-apply rule injected before the sticky rules.
        var firstInjected = prepared[prepared.Count - 3];
        Assert.Contains("file body", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(firstInjected).Content[0]).Text);

        // Stream-trigger rule is never injected.
        Assert.DoesNotContain(prepared, m => m is UserMessage u && u.Content.OfType<TextContent>().Any(t => t.Text.Contains("stream body")));
    }

    [Fact]
    public async Task PrepareMessages_ResetsMatchingAccumulator()
    {
        var engine = Engine([new Rule("r", "x", TriggerPattern: "todo")]);
        await engine.InterceptDeltaAsync(Delta("remember todo"));
        Assert.NotEmpty(engine.AccumulatedText);

        var messages = new List<AgentMessage> { AgentMessages.User("u") };
        await engine.PrepareMessagesAsync(messages, new AgentContext("sys", messages));

        Assert.Equal(string.Empty, engine.AccumulatedText);
    }

    [Fact]
    public async Task DisabledEngine_IsNoOp()
    {
        var engine = new RulesEngine(
            _ => Task.FromResult<IReadOnlyList<Rule>>([new Rule("r", "c", TriggerPattern: "x")]),
            new RulesOptions { Disabled = true });

        var decision = await engine.InterceptDeltaAsync(Delta("x"));
        Assert.Null(decision);

        var messages = new List<AgentMessage> { AgentMessages.User("u") };
        var prepared = await engine.PrepareMessagesAsync(messages, new AgentContext("sys", messages));
        Assert.Same(messages, prepared);
        Assert.Empty(engine.Rules);
    }

    [Fact]
    public async Task EmptyRuleSource_ProducesNoDecisionsAndNoInjection()
    {
        var engine = new RulesEngine(_ => Task.FromResult<IReadOnlyList<Rule>>([]), new RulesOptions());

        var decision = await engine.InterceptDeltaAsync(Delta("x"));
        Assert.Null(decision);

        var messages = new List<AgentMessage> { AgentMessages.User("u") };
        var prepared = await engine.PrepareMessagesAsync(messages, new AgentContext("sys", messages));
        Assert.Same(messages, prepared);
    }
}
