using Xunit;
using PiSharp.Extensions;
using PiSharp.Extensions.Testing;

namespace PiSharp.Plugins.ForeignCompat.Tests;

/// <summary>
/// Full-plugin integration (P11 plan phase 6): <see cref="ForeignCompatExtension"/>
/// initialized through the real <see cref="ExtensionManager"/> wiring — skill providers
/// land in <see cref="ExtensionRegistry.SkillProviders"/> and rule providers in
/// <see cref="ExtensionRegistry.RuleProviders"/> — then a mixed-repo discovery is
/// exercised end to end. Toggles default to enabled because the fixture's settings
/// service is unbound (P11 plan §9 defaults).
/// </summary>
public sealed class ExtensionIntegrationTests
{
    [Fact]
    public async Task InitializeRegistersAllSkillAndRuleProviders()
    {
        using var repo = new TempRepo();
        await using var fixture = await ExtensionTestFixture.Create(new ForeignCompatExtension())
            .WithCwd(repo.Root)
            .BuildAsync();

        Assert.Collection(fixture.Registry.SkillProviders.OrderBy(r => r.Id),
            registration => AssertProvider(registration, "skill-provider:claude", 80),
            registration => AssertProvider(registration, "skill-provider:cline", 50),
            registration => AssertProvider(registration, "skill-provider:codex", 70),
            registration => AssertProvider(registration, "skill-provider:cursor", 50),
            registration => AssertProvider(registration, "skill-provider:gemini", 60),
            registration => AssertProvider(registration, "skill-provider:github", 30),
            registration => AssertProvider(registration, "skill-provider:opencode", 55));

        Assert.Collection(fixture.Registry.RuleProviders.OrderBy(r => r.Id),
            registration => AssertProvider(registration, "rule-provider:cline", 50),
            registration => AssertProvider(registration, "rule-provider:copilot", 40),
            registration => AssertProvider(registration, "rule-provider:cursor", 50),
            registration => AssertProvider(registration, "rule-provider:gemini", 60),
            registration => AssertProvider(registration, "rule-provider:github", 30),
            registration => AssertProvider(registration, "rule-provider:repo", 20));
    }

    [Fact]
    public async Task MixedRepoYieldsForeignSkillsAndRules()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: Claude skill.
            ---
            Claude body.
            """);
        repo.WriteFile(".github/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: GitHub skill (loses dedup).
            ---
            GitHub body.
            """);
        repo.WriteFile(".clinerules", "Cline rules.");
        repo.WriteFile(".cursorrules", "Cursor rules.");
        repo.WriteFile(".cursor/rules/testing.mdc",
            """
            ---
            description: Testing
            alwaysApply: true
            ---
            Test body.
            """);

        await using var fixture = await ExtensionTestFixture.Create(new ForeignCompatExtension())
            .WithCwd(repo.Root)
            .BuildAsync();

        // Skills: both providers emit the colliding "foo" with their tier's SourcePriority
        // (P04's pipeline resolves the collision — higher wins, claude 80 > github 30).
        var claudeSkills = InRepo(await DiscoverSkillsAsync(fixture, "skill-provider:claude"), repo);
        var githubSkills = InRepo(await DiscoverSkillsAsync(fixture, "skill-provider:github"), repo);
        Assert.Equal(ForeignCompatTiers.Claude, Assert.Single(claudeSkills).SourcePriority);
        Assert.Equal(ForeignCompatTiers.Github, Assert.Single(githubSkills).SourcePriority);

        // Rules: each foreign format produced its normalized always-apply rule.
        var clineRules = InRepo(await DiscoverRulesAsync(fixture, "rule-provider:cline"), repo);
        var cursorRules = InRepo(await DiscoverRulesAsync(fixture, "rule-provider:cursor"), repo);
        Assert.Contains(clineRules, rule => rule.Name == "clinerules" && rule.Content == "Cline rules.");
        Assert.Contains(cursorRules, rule => rule.Name == "cursorrules" && rule.Content == "Cursor rules.");
        Assert.Contains(cursorRules, rule => rule.Name == "mdc:testing" && rule.Content == "Test body.");
    }

    [Fact]
    public async Task ToggleChangeIsHonoredOnNextDiscovery()
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
        await using var fixture = await ExtensionTestFixture.Create(new ForeignCompatExtension())
            .WithCwd(repo.Root)
            .BuildAsync();

        var before = InRepo(await DiscoverSkillsAsync(fixture, "skill-provider:claude"), repo);
        Assert.Single(before);

        // The fixture settings API is unbound, so toggles stay at their defaults; the
        // options-object path below covers the disabled case deterministically.
        var options = RepoOptions.For(repo, o => o.EnableClaudeUser = false);
        var disabled = await new ClaudeSkillProvider(options).DiscoverAsync();
        Assert.Empty(disabled);
    }

    private static void AssertProvider(OwnedExtensionRegistration<ISkillProvider> registration, string expectedKey, int expectedPriority)
    {
        Assert.Equal(expectedKey, registration.Id);
        Assert.Equal(expectedPriority, registration.Value.Priority);
    }

    private static void AssertProvider(OwnedExtensionRegistration<IRuleProvider> registration, string expectedKey, int expectedPriority)
    {
        Assert.Equal(expectedKey, registration.Id);
        Assert.Equal(expectedPriority, registration.Value.Priority);
    }

    private static IEnumerable<ExtensionSkillDefinition> InRepo(IEnumerable<ExtensionSkillDefinition> definitions, TempRepo repo)
        => definitions.Where(definition => definition.FilePath.StartsWith(repo.Root, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Rule> InRepo(IEnumerable<Rule> rules, TempRepo repo)
        => rules.Where(rule => rule.Path is not null && rule.Path.StartsWith(repo.Root, StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<ExtensionSkillDefinition>> DiscoverSkillsAsync(ExtensionTestFixture fixture, string providerKey)
    {
        var registration = fixture.Registry.SkillProviders.Single(r => r.Id == providerKey);
        return await registration.Value.DiscoverAsync();
    }

    private static async Task<IReadOnlyList<Rule>> DiscoverRulesAsync(ExtensionTestFixture fixture, string providerKey)
    {
        var registration = fixture.Registry.RuleProviders.Single(r => r.Id == providerKey);
        return await registration.Value.DiscoverAsync();
    }
}
