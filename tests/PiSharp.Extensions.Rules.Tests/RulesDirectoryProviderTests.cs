using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class RulesDirectoryProviderTests
{
    [Fact]
    public void ExposesPlannedNameAndPriority()
    {
        var provider = new RulesDirectoryProvider(["unused"]);

        Assert.Equal("rules-dir", provider.Name);
        Assert.Equal(100, provider.Priority);
    }

    [Fact]
    public async Task DiscoversMarkdownAndMdcFilesWithFrontmatter()
    {
        using var dir = new TempDir();
        var rulesDir = Path.Combine(dir.Root, ".pi", "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "no-todo.md"),
            "---\nname: no-todo\npattern: (?i)todo list\n---\nDon't add a todo list.");
        File.WriteAllText(Path.Combine(rulesDir, "always.mdc"),
            "---\nalways: true\n---\nAlways follow this.");

        var provider = new RulesDirectoryProvider([rulesDir]);
        var rules = await provider.DiscoverAsync();

        Assert.Equal(2, rules.Count);
        var stream = Assert.Single(rules, r => r.Name == "no-todo");
        Assert.Equal(RuleApplyMode.StreamTrigger, stream.ApplyMode);
        Assert.Equal("(?i)todo list", stream.TriggerPattern);
        Assert.Equal("Don't add a todo list.", stream.Content);
        Assert.Equal(0, stream.Priority);

        var always = Assert.Single(rules, r => r.Name == "always");
        Assert.Equal(RuleApplyMode.Always, always.ApplyMode);
        Assert.Null(always.TriggerPattern);
    }

    [Fact]
    public async Task SingleFileCandidateProducesARule()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("rules.md", "---\nalways: true\n---\nUser-level file.");

        var provider = new RulesDirectoryProvider([], singleFileCandidates: [file]);
        var rules = await provider.DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("rules", rule.Name);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
    }

    [Fact]
    public async Task SkipsFileWithNeitherAlwaysNorPattern_AndReportsWarning()
    {
        using var dir = new TempDir();
        var bad = dir.WriteFile("rules/bad.md", "no frontmatter, nothing");
        var warnings = new List<string>();
        var provider = new RulesDirectoryProvider([Path.GetDirectoryName(bad)!], onWarning: warnings.Add);

        var rules = await provider.DiscoverAsync();

        Assert.Empty(rules);
        Assert.Contains(warnings, w => w.Contains("neither 'always: true' nor a 'pattern'"));
    }

    [Fact]
    public async Task NearestRootWinsOnNameCollision()
    {
        using var dir = new TempDir();
        var projectRules = dir.WriteFile("project/.pi/rules/foo.md", "---\nalways: true\n---\nproject content");
        var userRoot = Path.Combine(dir.Root, "user-rules");
        var userRules = Path.Combine(userRoot, "foo.md");
        Directory.CreateDirectory(userRoot);
        File.WriteAllText(userRules, "---\nalways: true\n---\nuser content");

        // Nearest (project) root listed first; user root second. Both parse to rule "foo".
        var provider = new RulesDirectoryProvider([Path.GetDirectoryName(projectRules)!, userRoot]);
        var rules = await provider.DiscoverAsync();

        var rule = Assert.Single(rules);
        Assert.Equal("foo", rule.Name);
        Assert.Equal("project content", rule.Content);
    }

    [Fact]
    public async Task DeduplicatesTheSamePathAcrossRoots()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("rules/x.md", "---\nalways: true\n---\nX.");
        var rulesDir = Path.GetDirectoryName(file)!;

        var provider = new RulesDirectoryProvider([rulesDir, rulesDir]);
        var rules = await provider.DiscoverAsync();

        Assert.Single(rules);
    }
}
