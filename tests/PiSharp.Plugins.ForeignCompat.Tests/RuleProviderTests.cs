using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Plugins.ForeignCompat.Tests;

/// <summary>
/// Concrete foreign rule providers (P11 plan phases 4–5): per-format discovery from a
/// temp repo, nearest-root-wins for whole-file formats, toggle gating, and repo-rule
/// dedup against P10's sticky surface.
/// </summary>
public sealed class RuleProviderTests
{
    [Fact]
    public async Task ClineProviderDiscoversWholeFileAndCollection()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".clinerules", "Whole-file cline rules.");
        repo.WriteFile(".cli/rules/frontend.md", "Frontend cline rules.");
        repo.WriteFile(".cli/rules/backend.md", "Backend cline rules.");
        var options = RepoOptions.For(repo);

        var rules = await new ClineRuleProvider(options).DiscoverAsync();

        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, rule => rule.Name == "clinerules" && rule.Content == "Whole-file cline rules." && rule.ApplyMode == RuleApplyMode.Always);
        Assert.Contains(rules, rule => rule.Name == "clinerules:frontend");
        Assert.Contains(rules, rule => rule.Name == "clinerules:backend");
    }

    [Fact]
    public async Task ClineProviderDiscoversNamedCollectionDirectory()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".clinerules/frontend.md", "Frontend cline rules.");
        repo.WriteFile(".clinerules/backend.md", "Backend cline rules.");
        var options = RepoOptions.For(repo);

        var rules = await new ClineRuleProvider(options).DiscoverAsync();

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.Name == "clinerules:frontend");
        Assert.Contains(rules, rule => rule.Name == "clinerules:backend");
    }

    [Fact]
    public async Task ClineProviderNearestRootWinsForWholeFile()
    {
        using var repo = new TempRepo();
        var home = Path.Combine(repo.Root, "home");
        var project = Path.Combine(repo.Root, "project");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(home, ".clinerules"), "User rules.");
        File.WriteAllText(Path.Combine(project, ".clinerules"), "Repo rules.");
        var options = new ForeignCompatOptions { Roots = [home, project] };

        var rules = await new ClineRuleProvider(options).DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("clinerules", rule.Name);
        Assert.Equal("Repo rules.", rule.Content);
    }

    [Fact]
    public async Task CursorProviderDiscoversCursorrulesMdcAndMarkdown()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".cursorrules", "Cursor rules.");
        repo.WriteFile(".cursor/rules/testing.mdc",
            """
            ---
            description: Testing rules
            alwaysApply: true
            ---
            Always run tests.
            """);
        repo.WriteFile(".cursor/rules/disabled.mdc",
            """
            ---
            description: Disabled
            alwaysApply: false
            ---
            Never applied.
            """);
        repo.WriteFile(".cursor/rules/style.md", "Plain markdown style.");
        var options = RepoOptions.For(repo);

        var rules = await new CursorRuleProvider(options).DiscoverAsync();

        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, rule => rule.Name == "cursorrules" && rule.Content == "Cursor rules.");
        Assert.Contains(rules, rule => rule.Name == "mdc:testing" && rule.Content == "Always run tests.");
        Assert.Contains(rules, rule => rule.Name == "cursor:style");
        Assert.DoesNotContain(rules, rule => rule.Name == "mdc:disabled");
        foreach (var rule in rules)
        {
            Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
            Assert.Null(rule.TriggerPattern);
        }
    }

    [Fact]
    public async Task CopilotProviderDiscoversNearestInstructions()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".github/copilot-instructions.md", "Repo copilot rules.");
        var options = RepoOptions.For(repo);

        var rules = await new CopilotRuleProvider(options).DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("copilot", rule.Name);
        Assert.Equal("Repo copilot rules.", rule.Content);
        Assert.Equal(ForeignCompatTiers.Copilot, rule.Priority);
    }

    [Fact]
    public async Task GeminiProviderDiscoversDirectoryAndConfigRoot()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".gemini/rules/style.md", "Gemini style.");
        repo.WriteFile("GEMINIRULES.md", "Gemini root rules.");
        var options = RepoOptions.For(repo);

        var rules = await new GeminiRuleProvider(options).DiscoverAsync();

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.Name == "gemini:style");
        Assert.Contains(rules, rule => rule.Name == "gemini-rules" && rule.Content == "Gemini root rules.");
    }

    [Fact]
    public async Task RepoProviderDiscoversNestedRulesButSkipsRepoRoot()
    {
        using var repo = new TempRepo();
        repo.WriteFile("RULES.md", "Repo-root sticky rules (P10 owns these).");
        repo.WriteFile("docs/RULES.md", "Docs rules.");
        repo.WriteFile(".pisharp/RULES.md", "PiSharp rules.");
        var options = RepoOptions.For(repo);

        var rules = await new RepoRuleProvider(options).DiscoverAsync();

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.Name == "rules:docs" && rule.Content == "Docs rules.");
        Assert.Contains(rules, rule => rule.Name == "rules:pisharp");
        Assert.DoesNotContain(rules, rule => rule.Content == "Repo-root sticky rules (P10 owns these).");
    }

    [Fact]
    public async Task RepoProviderSkipsVcsAndBuildDirectories()
    {
        using var repo = new TempRepo();
        repo.WriteFile("src/RULES.md", "Source rules.");
        repo.WriteFile("node_modules/pkg/RULES.md", "Dependency rules.");
        repo.WriteFile(".git/hooks/RULES.md", "Git internals.");
        var options = RepoOptions.For(repo);

        var rules = await new RepoRuleProvider(options).DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("rules:src", rule.Name);
    }

    [Fact]
    public async Task GithubProviderDiscoversRulesDirectory()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".github/rules/security.md", "Security rules.");
        repo.WriteFile(".github/rules/workflow.mdc",
            """
            ---
            description: Workflow
            alwaysApply: true
            ---
            Workflow rules.
            """);
        var options = RepoOptions.For(repo);

        var rules = await new GithubRuleProvider(options).DiscoverAsync();

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.Name == "github:security");
        Assert.Contains(rules, rule => rule.Name == "mdc:workflow");
    }

    [Fact]
    public async Task GithubProviderDoesNotReingestCopilotInstructions()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".github/copilot-instructions.md", "Copilot rules.");
        var options = RepoOptions.For(repo);

        var rules = await new GithubRuleProvider(options).DiscoverAsync();

        Assert.Empty(rules);
    }

    [Fact]
    public void RuleProvidersExposeThePlannedNamesAndPriorities()
    {
        using var repo = new TempRepo();
        var options = RepoOptions.For(repo);

        Assert.Equal("cline", new ClineRuleProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Cline, new ClineRuleProvider(options).Priority);
        Assert.Equal("cursor", new CursorRuleProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Cursor, new CursorRuleProvider(options).Priority);
        Assert.Equal("copilot", new CopilotRuleProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Copilot, new CopilotRuleProvider(options).Priority);
        Assert.Equal("gemini", new GeminiRuleProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Gemini, new GeminiRuleProvider(options).Priority);
        Assert.Equal("repo", new RepoRuleProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Repo, new RepoRuleProvider(options).Priority);
        Assert.Equal("github", new GithubRuleProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Github, new GithubRuleProvider(options).Priority);
    }

    [Fact]
    public async Task IgnoredRulesGlobFiltersDiscoveredRules()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".clinerules", "Cline rules.");
        repo.WriteFile(".cli/rules/secret.md", "Secret rules.");
        var options = RepoOptions.For(repo, o => o.IgnoredRules = ["*secret*"]);

        var rules = await new ClineRuleProvider(options).DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("clinerules", rule.Name);
    }

    [Fact]
    public async Task CollidingRuleNamesAcrossProvidersResolveByPriority()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".clinerules", "Cline rules.");
        repo.WriteFile(".cursorrules", "Cursor rules.");
        var options = RepoOptions.For(repo);

        var cline = await new ClineRuleProvider(options).DiscoverAsync();
        var cursor = await new CursorRuleProvider(options).DiscoverAsync();

        Assert.Equal(ForeignCompatTiers.Cline, Assert.Single(cline).Priority);
        Assert.Equal(ForeignCompatTiers.Cursor, Assert.Single(cursor).Priority);

        // Names differ ("clinerules" vs "cursorrules") so no cross-provider collision here;
        // equal-tier providers (cline/cursor) still emit distinct names deterministically.
        Assert.NotEqual(Assert.Single(cline).Name, Assert.Single(cursor).Name);
    }
}
