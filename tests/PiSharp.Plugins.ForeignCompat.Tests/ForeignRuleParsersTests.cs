using Xunit;
using PiSharp.Extensions;

namespace PiSharp.Plugins.ForeignCompat.Tests;

/// <summary>
/// <see cref="ForeignRuleParsers"/>: per-format translation to <see cref="Rule"/>s with
/// stable names, always-apply mode, and no trigger patterns (P11 plan phases 4–5).
/// </summary>
public sealed class ForeignRuleParsersTests
{
    private const string Source = "cline";
    private const int Priority = 50;

    [Fact]
    public void ParseClineRulesWholeFile()
    {
        var rules = ForeignRuleParsers.ParseClineRules("Always follow this.", @"C:\repo\.clinerules", Source, Priority);

        var rule = Assert.Single(rules);
        Assert.Equal("clinerules", rule.Name);
        Assert.Equal("Always follow this.", rule.Content);
        Assert.Equal(@"C:\repo\.clinerules", rule.Path);
        Assert.Equal(Priority, rule.Priority);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
        Assert.Null(rule.TriggerPattern);
    }

    [Fact]
    public void ParseClineRulesCollectionEntry()
    {
        var rules = ForeignRuleParsers.ParseClineRules("Frontend rules.", @"C:\repo\.clinerules\frontend.md", Source, Priority);

        var rule = Assert.Single(rules);
        Assert.Equal("clinerules:frontend", rule.Name);
    }

    [Fact]
    public void ParseClineRulesCliRulesDirectoryEntry()
    {
        var rules = ForeignRuleParsers.ParseClineRules("Backend rules.", @"C:\repo\.cli\rules\Backend.md", Source, Priority);

        var rule = Assert.Single(rules);
        Assert.Equal("clinerules:backend", rule.Name);
    }

    [Fact]
    public void ParseCursorRules()
    {
        var rules = ForeignRuleParsers.ParseCursorRules("Cursor house style.", @"C:\repo\.cursorrules", "cursor", 50);

        var rule = Assert.Single(rules);
        Assert.Equal("cursorrules", rule.Name);
        Assert.Equal("Cursor house style.", rule.Content);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
    }

    [Fact]
    public void ParseMdcAppliesRuleFromFrontmatterAndBody()
    {
        const string mdc =
            """
            ---
            description: Testing rules
            globs: "**/*.ts"
            alwaysApply: true
            ---
            Always run tests.
            """;
        var rules = ForeignRuleParsers.ParseMdc(mdc, @"C:\repo\.cursor\rules\testing.mdc", "cursor", 50);

        var rule = Assert.Single(rules);
        Assert.Equal("mdc:testing", rule.Name);
        Assert.Equal("Always run tests.", rule.Content);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
        Assert.Null(rule.TriggerPattern);
    }

    [Fact]
    public void ParseMdcSkipsRuleWhenAlwaysApplyIsFalse()
    {
        const string mdc =
            """
            ---
            description: Scoped only
            globs: "**/*.ts"
            alwaysApply: false
            ---
            Only in matching files.
            """;
        var rules = ForeignRuleParsers.ParseMdc(mdc, @"C:\repo\.cursor\rules\scoped.mdc", "cursor", 50);

        Assert.Empty(rules);
    }

    [Fact]
    public void ParseMdcDefaultsToApplyWhenAlwaysApplyMissing()
    {
        const string mdc =
            """
            ---
            description: No flag
            ---
            Applies anyway.
            """;
        var rules = ForeignRuleParsers.ParseMdc(mdc, @"C:\repo\.cursor\rules\noflag.mdc", "cursor", 50);

        var rule = Assert.Single(rules);
        Assert.Equal("mdc:noflag", rule.Name);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
    }

    [Fact]
    public void ParseCopilotApplyTo()
    {
        var rules = ForeignRuleParsers.ParseCopilotApplyTo("Copilot guidance.", @"C:\repo\.github\copilot-instructions.md", "copilot", 40);

        var rule = Assert.Single(rules);
        Assert.Equal("copilot", rule.Name);
        Assert.Equal("Copilot guidance.", rule.Content);
        Assert.Equal(40, rule.Priority);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
    }

    [Fact]
    public void ParseGeminiRulesConfigRoot()
    {
        var rules = ForeignRuleParsers.ParseGeminiRules("Gemini root rules.", @"C:\repo\GEMINIRULES.md", "gemini", 60);

        var rule = Assert.Single(rules);
        Assert.Equal("gemini-rules", rule.Name);
    }

    [Fact]
    public void ParseGeminiRulesDirectoryEntry()
    {
        var rules = ForeignRuleParsers.ParseGeminiRules("Gemini entry.", @"C:\repo\.gemini\rules\style.md", "gemini", 60);

        var rule = Assert.Single(rules);
        Assert.Equal("gemini:style", rule.Name);
    }

    [Fact]
    public void ParseGithubRulesDirectoryEntry()
    {
        var rules = ForeignRuleParsers.ParseGithubRules("GitHub rules.", @"C:\repo\.github\rules\security.md", "github", 30);

        var rule = Assert.Single(rules);
        Assert.Equal("github:security", rule.Name);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
    }

    [Fact]
    public void ParseRepoRulesNestedFile()
    {
        var rules = ForeignRuleParsers.ParseRepoRules("Docs rules.", @"C:\repo\docs\RULES.md", "repo", 20, @"C:\repo");

        var rule = Assert.Single(rules);
        Assert.Equal("rules:docs", rule.Name);
    }

    [Fact]
    public void ParseRepoRulesPisharpFile()
    {
        var rules = ForeignRuleParsers.ParseRepoRules("PiSharp rules.", @"C:\repo\.pisharp\RULES.md", "repo", 20, @"C:\repo");

        var rule = Assert.Single(rules);
        Assert.Equal("rules:pisharp", rule.Name);
    }

    [Fact]
    public void ParseRepoRulesRootFile()
    {
        var rules = ForeignRuleParsers.ParseRepoRules("Root rules.", @"C:\repo\RULES.md", "repo", 20, @"C:\repo");

        var rule = Assert.Single(rules);
        Assert.Equal("rules", rule.Name);
    }

    [Fact]
    public void ParsePlainMarkdown()
    {
        var rules = ForeignRuleParsers.ParsePlainMarkdown("Cursor markdown rule.", @"C:\repo\.cursor\rules\style.md", "cursor", 50, "cursor");

        var rule = Assert.Single(rules);
        Assert.Equal("cursor:style", rule.Name);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
    }

    [Fact]
    public void AllParsedRulesAreAlwaysApplyWithoutTriggerPattern()
    {
        var all = new List<Rule>();
        all.AddRange(ForeignRuleParsers.ParseClineRules("a", "clinerules", Source, Priority));
        all.AddRange(ForeignRuleParsers.ParseClineRules("b", @"x\.clinerules\frontend.md", Source, Priority));
        all.AddRange(ForeignRuleParsers.ParseCursorRules("c", ".cursorrules", "cursor", 50));
        all.AddRange(ForeignRuleParsers.ParseMdc("---\ndescription: d\n---\nbody", "x.mdc", "cursor", 50));
        all.AddRange(ForeignRuleParsers.ParseCopilotApplyTo("e", "copilot.md", "copilot", 40));
        all.AddRange(ForeignRuleParsers.ParseGeminiRules("f", "GEMINIRULES.md", "gemini", 60));
        all.AddRange(ForeignRuleParsers.ParseGithubRules("g", "x.md", "github", 30));
        all.AddRange(ForeignRuleParsers.ParseRepoRules("h", @"C:\repo\docs\RULES.md", "repo", 20, @"C:\repo"));

        Assert.NotEmpty(all);
        foreach (var rule in all)
        {
            Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
            Assert.Null(rule.TriggerPattern);
        }
    }
}
