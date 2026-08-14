using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Plugins.ForeignCompat.Tests;

/// <summary>
/// Concrete foreign skill providers (P11 plan phase 2/3): directory-to-definition
/// mapping, tier ladder, toggle gating, and the first-wins dedup contract by
/// <see cref="ExtensionSkillDefinition.SourcePriority"/>.
/// </summary>
public sealed class SkillProviderTests
{
    [Fact]
    public void ProvidersExposeThePlannedTierLadder()
    {
        using var repo = new TempRepo();
        var options = RepoOptions.For(repo);

        Assert.Equal("claude", new ClaudeSkillProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Claude, new ClaudeSkillProvider(options).Priority);
        Assert.Equal("codex", new CodexSkillProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Codex, new CodexSkillProvider(options).Priority);
        Assert.Equal("gemini", new GeminiSkillProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Gemini, new GeminiSkillProvider(options).Priority);
        Assert.Equal("opencode", new OpenCodeSkillProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.OpenCode, new OpenCodeSkillProvider(options).Priority);
        Assert.Equal("cursor", new CursorSkillProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Cursor, new CursorSkillProvider(options).Priority);
        Assert.Equal("cline", new ClineSkillProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Cline, new ClineSkillProvider(options).Priority);
        Assert.Equal("github", new GithubSkillProvider(options).Name);
        Assert.Equal(ForeignCompatTiers.Github, new GithubSkillProvider(options).Priority);

        // The ladder ordering itself: claude > codex > gemini > opencode > cursor/cline > github
        Assert.True(ForeignCompatTiers.Claude > ForeignCompatTiers.Codex);
        Assert.True(ForeignCompatTiers.Codex > ForeignCompatTiers.Gemini);
        Assert.True(ForeignCompatTiers.Gemini > ForeignCompatTiers.OpenCode);
        Assert.True(ForeignCompatTiers.OpenCode > ForeignCompatTiers.Cursor);
        Assert.Equal(ForeignCompatTiers.Cursor, ForeignCompatTiers.Cline);
        Assert.True(ForeignCompatTiers.Cline > ForeignCompatTiers.Github);
    }

    [Fact]
    public async Task ClaudeProviderDiscoversItsOwnRoot()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: Claude skill.
            ---
            Body.
            """);
        repo.WriteFile(".codex/skills/bar/SKILL.md",
            """
            ---
            name: bar
            description: Codex skill.
            ---
            Body.
            """);

        var definitions = await new ClaudeSkillProvider(RepoOptions.For(repo)).DiscoverAsync();

        var definition = Assert.Single(definitions);
        Assert.Equal("foo", definition.Name);
        Assert.Equal("claude", definition.Source);
        Assert.Equal(ForeignCompatTiers.Claude, definition.SourcePriority);
    }

    [Fact]
    public async Task EveryProviderDiscoversItsOwnRoot()
    {
        using var repo = new TempRepo();
        var options = RepoOptions.For(repo);
        var cases = new (ISkillProvider Provider, string ToolDir, string Source)[]
        {
            (new CodexSkillProvider(options), ".codex", "codex"),
            (new OpenCodeSkillProvider(options), ".opencode", "opencode"),
            (new GithubSkillProvider(options), ".github", "github"),
            (new CursorSkillProvider(options), ".cursor", "cursor"),
            (new ClineSkillProvider(options), ".cline", "cline"),
            (new GeminiSkillProvider(options), ".gemini", "gemini"),
        };

        foreach (var (provider, toolDir, source) in cases)
        {
            repo.WriteFile($"{toolDir}/skills/alpha/SKILL.md",
                $"""
                ---
                name: alpha
                description: {source} skill.
                ---
                Body.
                """);
            var definitions = await provider.DiscoverAsync();
            var definition = Assert.Single(definitions);
            Assert.Equal("alpha", definition.Name);
            Assert.Equal(source, definition.Source);
            Assert.Equal(provider.Priority, definition.SourcePriority);
            repo.DeleteFile($"{toolDir}/skills/alpha/SKILL.md");
        }
    }

    [Fact]
    public async Task DisabledToggleEmptiesDiscovery()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: Claude skill.
            ---
            Body.
            """);
        var options = RepoOptions.For(repo, o => o.EnableClaudeUser = false);

        var definitions = await new ClaudeSkillProvider(options).DiscoverAsync();

        Assert.Empty(definitions);
    }

    [Fact]
    public async Task DisabledTogglesEmptyEveryProvider()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: Claude skill.
            ---
            Body.
            """);
        repo.WriteFile(".clinerules", "Cline rule.");
        var options = RepoOptions.For(repo, o =>
        {
            o.EnableClaudeUser = false;
            o.EnableCodexUser = false;
            o.EnableOpenCode = false;
            o.EnableGithubUser = false;
            o.EnableCursorUser = false;
            o.EnableClineUser = false;
            o.EnableGeminiUser = false;
            o.EnableCopilotUser = false;
            o.EnableRepoRules = false;
        });

        ISkillProvider[] skillProviders =
        [
            new ClaudeSkillProvider(options),
            new CodexSkillProvider(options),
            new OpenCodeSkillProvider(options),
            new GithubSkillProvider(options),
            new CursorSkillProvider(options),
            new ClineSkillProvider(options),
            new GeminiSkillProvider(options),
        ];
        IRuleProvider[] ruleProviders =
        [
            new ClineRuleProvider(options),
            new CursorRuleProvider(options),
            new CopilotRuleProvider(options),
            new GeminiRuleProvider(options),
            new RepoRuleProvider(options),
            new GithubRuleProvider(options),
        ];

        foreach (var provider in skillProviders)
            Assert.Empty(await provider.DiscoverAsync());
        foreach (var provider in ruleProviders)
            Assert.Empty(await provider.DiscoverAsync());
    }

    [Fact]
    public async Task CollidingSkillNamesResolveBySourcePriority()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: Claude wins.
            ---
            Body.
            """);
        repo.WriteFile(".github/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: GitHub loses.
            ---
            Body.
            """);
        var options = RepoOptions.For(repo);

        var claude = await new ClaudeSkillProvider(options).DiscoverAsync();
        var github = await new GithubSkillProvider(options).DiscoverAsync();

        Assert.Equal(ForeignCompatTiers.Claude, Assert.Single(claude).SourcePriority);
        Assert.Equal(ForeignCompatTiers.Github, Assert.Single(github).SourcePriority);

        // First-wins dedup contract (P04): higher SourcePriority wins on name collision.
        var winner = MergeFirstWins(claude.Concat(github));
        var merged = Assert.Single(winner);
        Assert.Equal("foo", merged.Name);
        Assert.Equal("claude", merged.Source);
        Assert.Equal(ForeignCompatTiers.Claude, merged.SourcePriority);
    }

    private static IReadOnlyList<ExtensionSkillDefinition> MergeFirstWins(IEnumerable<ExtensionSkillDefinition> definitions)
    {
        var winners = new Dictionary<string, ExtensionSkillDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!winners.TryGetValue(definition.Name, out var existing) || definition.SourcePriority > existing.SourcePriority)
                winners[definition.Name] = definition;
        }
        return winners.Values.ToArray();
    }
}
