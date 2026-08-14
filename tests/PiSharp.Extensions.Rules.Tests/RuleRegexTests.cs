using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class RuleRegexTests
{
    [Fact]
    public void CaseSensitiveByDefault()
    {
        var rule = new Rule("r", "body", TriggerPattern: "todo list", ApplyMode: RuleApplyMode.StreamTrigger);
        Assert.True(RuleRegex.Matches(rule, "remember the todo list"));
        Assert.False(RuleRegex.Matches(rule, "remember the TODO LIST"));
    }

    [Fact]
    public void InlineIgnoreCaseOptIn()
    {
        var rule = new Rule("r", "body", TriggerPattern: "(?i)todo list", ApplyMode: RuleApplyMode.StreamTrigger);
        Assert.True(RuleRegex.Matches(rule, "remember the TODO LIST"));
    }

    [Fact]
    public void NullOrBlankPattern_ReturnsFalse()
    {
        Assert.False(RuleRegex.Matches((string?)null, "any text"));
        Assert.False(RuleRegex.Matches("  ", "any text"));
        Assert.False(RuleRegex.Matches(new Rule("r", "b", ApplyMode: RuleApplyMode.Always), "any text"));
    }

    [Fact]
    public void InvalidPattern_ReturnsFalseWithoutThrowing()
    {
        Assert.False(RuleRegex.Matches("(unclosed", "any text"));
    }

    [Fact]
    public void CatastrophicBacktracking_TimesOutAndReturnsFalseInsteadOfThrowing()
    {
        // (a+)+$ against non-matching input triggers exponential backtracking that exceeds
        // the 250 ms MatchTimeout; the guard must return no-match, never throw.
        var rule = new Rule("r", "body", TriggerPattern: "(a+)+$", ApplyMode: RuleApplyMode.StreamTrigger);
        var exception = Record.Exception(() =>
            Assert.False(RuleRegex.Matches(rule, new string('a', 24) + "!")));
        Assert.Null(exception);
    }

    [Fact]
    public void Build_ProducesRegexWithTimeout()
    {
        var regex = RuleRegex.Build("todo");
        Assert.Equal(RuleRegex.MatchTimeout, regex.MatchTimeout);
        Assert.Matches(regex, "a todo");
    }
}
