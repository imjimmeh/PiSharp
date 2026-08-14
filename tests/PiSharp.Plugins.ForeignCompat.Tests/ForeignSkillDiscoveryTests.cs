using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Plugins.ForeignCompat.Tests;

/// <summary>
/// <see cref="ForeignSkillDiscovery"/>: SKILL.md walk + frontmatter → definitions,
/// canonical-path dedup, and include/ignore glob filtering (P11 plan phases 1–3).
/// </summary>
public sealed class ForeignSkillDiscoveryTests
{
    [Fact]
    public async Task DiscoverMapsSkillMdToDefinition()
    {
        using var repo = new TempRepo();
        var path = repo.WriteFile(".claude/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: Does foo things.
            disable-model-invocation: true
            ---
            Instructions for foo.
            """);

        var definitions = await Discover(repo, "claude", ForeignCompatTiers.Claude);

        var definition = Assert.Single(definitions);
        Assert.Equal("foo", definition.Name);
        Assert.Equal("Does foo things.", definition.Description);
        Assert.Equal("Instructions for foo.", definition.Content.Trim());
        Assert.Equal(path, definition.FilePath);
        Assert.True(definition.DisableModelInvocation);
        Assert.Equal("claude", definition.Source);
        Assert.Equal(ForeignCompatTiers.Claude, definition.SourcePriority);
    }

    [Fact]
    public async Task DiscoverFallsBackToDirectoryName()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".codex/skills/bar/SKILL.md",
            """
            ---
            description: Does bar things.
            ---
            Instructions for bar.
            """);

        var definitions = await Discover(repo, "codex", ForeignCompatTiers.Codex);

        var definition = Assert.Single(definitions);
        Assert.Equal("bar", definition.Name);
        Assert.False(definition.DisableModelInvocation);
    }

    [Fact]
    public async Task DiscoverSkipsSkillWithoutDescription()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/quiet/SKILL.md",
            """
            ---
            name: quiet
            ---
            No description.
            """);
        repo.WriteFile(".claude/skills/loud/SKILL.md",
            """
            ---
            name: loud
            description: Has one.
            ---
            Body.
            """);

        var definitions = await Discover(repo, "claude", ForeignCompatTiers.Claude);

        var definition = Assert.Single(definitions);
        Assert.Equal("loud", definition.Name);
    }

    [Fact]
    public async Task DiscoverDedupsByCanonicalPath()
    {
        using var repo = new TempRepo();
        var path = repo.WriteFile(".claude/skills/foo/SKILL.md",
            """
            ---
            name: foo
            description: Dup.
            ---
            Body.
            """);
        var options = RepoOptions.For(repo);
        // Same file reachable from two roots — must be emitted once.
        var roots = new[] { repo.Root, repo.Root };

        var definitions = await ForeignSkillDiscovery.DiscoverFromRootsAsync(
            ForeignPaths.DiscoverSkillDirs(roots, ".claude"), "claude", 80, options);

        var definition = Assert.Single(definitions);
        Assert.Equal(path, definition.FilePath);
    }

    [Fact]
    public async Task DiscoverAppliesIgnoredSkillsGlob()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/keep-me/SKILL.md",
            """
            ---
            name: keep-me
            description: Keep.
            ---
            Body.
            """);
        repo.WriteFile(".claude/skills/drop-me/SKILL.md",
            """
            ---
            name: drop-me
            description: Drop.
            ---
            Body.
            """);
        var options = RepoOptions.For(repo, o => o.IgnoredSkills = ["*drop-me*"]);

        var definitions = await ForeignSkillDiscovery.DiscoverFromRootsAsync(
            ForeignPaths.DiscoverSkillDirs(options.Roots, ".claude"), "claude", 80, options);

        var definition = Assert.Single(definitions);
        Assert.Equal("keep-me", definition.Name);
    }

    [Fact]
    public async Task DiscoverAppliesIncludeSkillsGlob()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/alpha/SKILL.md",
            """
            ---
            name: alpha
            description: A.
            ---
            Body.
            """);
        repo.WriteFile(".claude/skills/beta/SKILL.md",
            """
            ---
            name: beta
            description: B.
            ---
            Body.
            """);
        var options = RepoOptions.For(repo, o => o.IncludeSkills = ["alpha"]);

        var definitions = await ForeignSkillDiscovery.DiscoverFromRootsAsync(
            ForeignPaths.DiscoverSkillDirs(options.Roots, ".claude"), "claude", 80, options);

        var definition = Assert.Single(definitions);
        Assert.Equal("alpha", definition.Name);
    }

    [Fact]
    public async Task DiscoverDropsByPathWhenIgnoredGlobMatches()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".claude/skills/secret/alpha/SKILL.md",
            """
            ---
            name: alpha
            description: A.
            ---
            Body.
            """);
        repo.WriteFile(".claude/skills/public/beta/SKILL.md",
            """
            ---
            name: beta
            description: B.
            ---
            Body.
            """);
        // ignored glob matches the path of the first skill, not its name
        var options = RepoOptions.For(repo, o => o.IgnoredSkills = ["**/secret/**"]);

        var definitions = await ForeignSkillDiscovery.DiscoverFromRootsAsync(
            ForeignPaths.DiscoverSkillDirs(options.Roots, ".claude"), "claude", 80, options);

        var definition = Assert.Single(definitions);
        Assert.Equal("beta", definition.Name);
    }

    [Fact]
    public void ParseFrontmatterFallsBackToWholeContentWithoutDelimiters()
    {
        var (frontmatter, body) = ForeignSkillDiscovery.ParseSkillFrontmatter("Just a body, no frontmatter.");

        Assert.Empty(frontmatter);
        Assert.Equal("Just a body, no frontmatter.", body);
    }

    [Fact]
    public void ParseFrontmatterReadsUnderscoredKeys()
    {
        var (frontmatter, body) = ForeignSkillDiscovery.ParseSkillFrontmatter(
            """
            ---
            name: n
            description: d
            disable-model-invocation: true
            ---
            Body text.
            """);

        Assert.Equal("n", frontmatter["name"]?.ToString());
        Assert.Equal("d", frontmatter["description"]?.ToString());
        // YamlDotNet may yield "true" as string or bool; both must read as truthy.
        Assert.Equal("true", frontmatter["disable-model-invocation"]?.ToString(), ignoreCase: true);
        Assert.Equal("Body text.", body);
    }

    private static async Task<IReadOnlyList<ExtensionSkillDefinition>> Discover(TempRepo repo, string source, int priority)
    {
        var options = RepoOptions.For(repo);
        var roots = ForeignPaths.DiscoverSkillDirs(options.Roots, $".{source}");
        return await ForeignSkillDiscovery.DiscoverFromRootsAsync(roots, source, priority, options);
    }
}
