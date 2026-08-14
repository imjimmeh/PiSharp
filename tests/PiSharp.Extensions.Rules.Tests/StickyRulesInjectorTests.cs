using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class StickyRulesInjectorTests
{
    [Fact]
    public void Format_WrapsContentInRulesBlock()
    {
        var rule = new Rule("RULES", "Body text.", Path: "C:/users/x/.pi/agent/RULES.md", ApplyMode: RuleApplyMode.Always);
        var formatted = StickyRulesInjector.Format(rule);

        Assert.Contains("<rules name=\"RULES\"", formatted);
        Assert.Contains("location=\"C:/users/x/.pi/agent/RULES.md\"", formatted);
        Assert.Contains("Body text.", formatted);
        Assert.Contains("</rules>", formatted);
    }

    [Fact]
    public void Format_NormalizesBackslashesInLocation()
    {
        var rule = new Rule("R", "x", Path: @"C:\repo\RULES.md", ApplyMode: RuleApplyMode.Always);
        var formatted = StickyRulesInjector.Format(rule);

        Assert.Contains("C:/repo/RULES.md", formatted);
        Assert.DoesNotContain("\\repo", formatted);
    }

    [Fact]
    public void FormatSticky_ProducesAlwaysRuleStyle()
    {
        var formatted = StickyRulesInjector.FormatSticky("RULES@project", "Project body.", "proj/RULES.md");

        Assert.Contains("<rules name=\"RULES@project\"", formatted);
        Assert.Contains("location=\"proj/RULES.md\"", formatted);
        Assert.Contains("Project body.", formatted);
    }
}
