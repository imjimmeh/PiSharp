using PiSharp.Subagents.AgentDefinitions;
using PiSharp.Subagents.Discovery;
using Xunit;

namespace PiSharp.Subagents.Tests;

public sealed class AgentDefinitionDiscoveryTests : IDisposable
{
    private readonly string _tempRoot;

    public AgentDefinitionDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pisharp-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    private string AgentsDir(string tier) => Path.Combine(_tempRoot, tier);

    private void WriteAgent(string relativeDir, string name, string description)
    {
        var dir = Path.Combine(_tempRoot, relativeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}.md");
        File.WriteAllText(path, $$"""
            ---
            name: {{name}}
            description: {{description}}
            ---

            You are {{name}}.
            """);
    }

    [Fact]
    public void DiscoverProjectWinsOverUserExtensionAndBundled()
    {
        WriteAgent("project", "task", "project task");
        WriteAgent("user", "task", "user task");
        WriteAgent("ext", "task", "ext task");

        var discovery = new AgentDefinitionDiscovery(
            projectDirs: [AgentsDir("project")],
            userDirs: [AgentsDir("user")],
            extensionDirs: [AgentsDir("ext")]);

        var result = discovery.Discover();

        var definition = Assert.Single(result.Values, value => value.Name == "task");
        Assert.Equal("project task", definition.Description);
        Assert.Equal(AgentSourceKind.Project, definition.Source);
    }

    [Fact]
    public void DiscoverUserOverridesExtensionAndBundled()
    {
        WriteAgent("user", "reviewer", "user reviewer");
        WriteAgent("ext", "reviewer", "ext reviewer");

        var discovery = new AgentDefinitionDiscovery(
            projectDirs: [AgentsDir("project")],
            userDirs: [AgentsDir("user")],
            extensionDirs: [AgentsDir("ext")]);

        var result = discovery.Discover();

        Assert.Equal("user reviewer", result["reviewer"].Description);
        Assert.Equal(AgentSourceKind.User, result["reviewer"].Source);
    }

    [Fact]
    public void DiscoverExtensionOverridesBundled()
    {
        WriteAgent("ext", "scout", "ext scout");

        var discovery = new AgentDefinitionDiscovery(extensionDirs: [AgentsDir("ext")]);

        var result = discovery.Discover();

        Assert.Equal("ext scout", result["scout"].Description);
        Assert.Equal(AgentSourceKind.Extension, result["scout"].Source);
    }

    [Fact]
    public void DiscoverBundledFillsAgentsNotOverridden()
    {
        var discovery = new AgentDefinitionDiscovery();

        var result = discovery.Discover();

        Assert.Contains("task", result.Keys);
        Assert.Contains("librarian", result.Keys);
        Assert.Equal(AgentSourceKind.Bundled, result["librarian"].Source);
    }

    [Fact]
    public void DiscoverNeverProducesDuplicateNames()
    {
        WriteAgent("project", "dup", "project dup");
        WriteAgent("user", "dup", "user dup");
        WriteAgent("ext", "dup", "ext dup");

        var discovery = new AgentDefinitionDiscovery(
            projectDirs: [AgentsDir("project")],
            userDirs: [AgentsDir("user")],
            extensionDirs: [AgentsDir("ext")]);

        var result = discovery.Discover();

        Assert.Single(result.Values, value => value.Name == "dup");
    }

    [Fact]
    public void DiscoverSkipsInvalidFilesWithoutDroppingValidOnes()
    {
        var projectDir = AgentsDir("project");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "broken.md"), "---\ndescription: no name\n---\nbody");
        WriteAgent("project", "valid", "valid agent");

        var discovery = new AgentDefinitionDiscovery(projectDirs: [projectDir]);

        var result = discovery.Discover();

        Assert.Contains("valid", result.Keys);
        Assert.DoesNotContain("broken", result.Keys);
        Assert.Equal(8, result.Count); // 7 bundled + 1 project
    }

    [Fact]
    public void RegistryTryGetIsCaseSensitiveExact()
    {
        var registry = new AgentDefinitionRegistry();
        WriteAgent("project", "CaseAgent", "case agent");
        registry.Replace(new AgentDefinitionDiscovery(projectDirs: [AgentsDir("project")]).Discover());

        Assert.NotNull(registry.TryGet("CaseAgent"));
        Assert.Null(registry.TryGet("caseagent"));
        Assert.Null(registry.TryGet("Caseagent"));
    }

    [Fact]
    public void RegistryListVisibleFiltersHideAndDisabled()
    {
        var projectDir = AgentsDir("project");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "visible.md"), """
            ---
            name: visible
            description: Shows up.
            ---

            body
            """);
        File.WriteAllText(Path.Combine(projectDir, "hidden.md"), """
            ---
            name: hidden
            description: Hidden from listing.
            hide: true
            ---

            body
            """);
        File.WriteAllText(Path.Combine(projectDir, "disabled.md"), """
            ---
            name: disabled
            description: Disabled agent.
            ---

            body
            """);

        var registry = new AgentDefinitionRegistry();
        registry.Replace(
            new AgentDefinitionDiscovery(projectDirs: [projectDir]).Discover(),
            new HashSet<string>(["disabled"], StringComparer.Ordinal));

        var names = registry.ListVisible().Select(definition => definition.Name).ToArray();

        Assert.Equal(
            ["designer", "librarian", "reviewer", "scout", "security-reviewer", "sonic", "task", "visible"],
            names);
        Assert.True(registry.IsDisabled("disabled"));
        Assert.False(registry.IsDisabled("visible"));
        // Hidden and disabled entries stay spawnable by explicit name.
        Assert.NotNull(registry.TryGet("hidden"));
        Assert.NotNull(registry.TryGet("disabled"));
    }
}
