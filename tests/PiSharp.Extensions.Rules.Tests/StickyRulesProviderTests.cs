using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class StickyRulesProviderTests
{
    [Fact]
    public void ExposesPlannedNameAndPriority()
    {
        var provider = new StickyRulesProvider(null, null);
        Assert.Equal("rules-sticky", provider.Name);
        Assert.Equal(1000, provider.Priority);
    }

    [Fact]
    public async Task UserFileSynthesizesRulesRule()
    {
        using var dir = new TempDir();
        var userFile = dir.WriteFile("agent/RULES.md", "User sticky rules.");
        var provider = new StickyRulesProvider(userFile, null);

        var rules = await provider.DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("RULES", rule.Name);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
        Assert.Equal("User sticky rules.", rule.Content);
        Assert.Equal(userFile, rule.Path);
    }

    [Fact]
    public async Task ProjectAncestorWalk_StopsAtFirstNonEmpty()
    {
        using var dir = new TempDir();
        // Two candidates: parent has a non-empty RULES.md, child (closer) has an EMPTY one.
        var parentRules = dir.WriteFile("a/RULES.md", "parent project rules");
        var childDir = Path.Combine(dir.Root, "a", "b");
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, "RULES.md"), "   ");

        var provider = new StickyRulesProvider(null, childDir);
        var rules = await provider.DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("RULES@project", rule.Name);
        Assert.Equal(parentRules, rule.Path);
    }

    [Fact]
    public async Task BothUserAndProject_SynthesizeProjectAfterUser()
    {
        using var dir = new TempDir();
        var userFile = dir.WriteFile("agent/RULES.md", "user");
        var projectRules = dir.WriteFile("proj/RULES.md", "project");

        var provider = new StickyRulesProvider(userFile, Path.Combine(dir.Root, "proj"));
        var rules = await provider.DiscoverAsync();

        Assert.Equal(2, rules.Count);
        Assert.Equal("RULES", rules[0].Name);
        Assert.Equal("RULES@project", rules[1].Name);
    }

    [Fact]
    public async Task MissingOrEmptyFiles_ProduceNoRules()
    {
        using var dir = new TempDir();
        var emptyUser = dir.WriteFile("agent/RULES.md", " \n ");
        var provider = new StickyRulesProvider(emptyUser, Path.Combine(dir.Root, "no-such-dir"));

        var rules = await provider.DiscoverAsync();

        Assert.Empty(rules);
    }

    [Fact]
    public async Task DisableSticky_ReturnsNoRules()
    {
        using var dir = new TempDir();
        var userFile = dir.WriteFile("agent/RULES.md", "user");
        var provider = new StickyRulesProvider(userFile, null, disableSticky: true);

        var rules = await provider.DiscoverAsync();

        Assert.Empty(rules);
    }
}
