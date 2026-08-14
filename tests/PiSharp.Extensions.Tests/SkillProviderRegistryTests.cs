using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// P04 (GAP-56): structured skill definitions, skill-provider first-wins dedup,
/// and the per-skill runner hook in the extension registry.
/// </summary>
public sealed class SkillProviderRegistryTests
{
    [Fact]
    public void RegisterSkillRoundTripsRicherDefinitionMetadata()
    {
        var registry = new ExtensionRegistry();
        ExtensionSkillRunner runner = (_, _) => Task.FromResult(new ExtensionSkillRunResult("ran"));

        registry.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "structured",
            "Structured skill",
            "body",
            "/repo/structured/SKILL.md",
            DisableModelInvocation: true,
            Override: ExtensionOverridePolicy.Override,
            Globs: ["**/*.cs", "**/*.md"],
            AlwaysApply: true,
            Hide: true,
            Source: "extension:test",
            SourcePriority: 7,
            Runner: runner));

        var skill = Assert.Single(registry.Skills).Value;
        Assert.Equal("structured", skill.Name);
        Assert.Equal("Structured skill", skill.Description);
        Assert.Equal("body", skill.Content);
        Assert.Equal("/repo/structured/SKILL.md", skill.FilePath);
        Assert.True(skill.DisableModelInvocation);
        Assert.Equal(ExtensionOverridePolicy.Override, skill.Override);
        Assert.Equal(["**/*.cs", "**/*.md"], skill.Globs);
        Assert.True(skill.AlwaysApply);
        Assert.True(skill.Hide);
        Assert.Equal("extension:test", skill.Source);
        Assert.Equal(7, skill.SourcePriority);
        Assert.Same(runner, skill.Runner);
    }

    [Fact]
    public void RegisterSkillStoresRunnerInSkillRunnerMapAndDisposeRemovesBoth()
    {
        var registry = new ExtensionRegistry();
        ExtensionSkillRunner runner = (_, _) => Task.FromResult(new ExtensionSkillRunResult("ran"));

        var handle = registry.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "runnable", "Runnable", "body", "/repo/runnable/SKILL.md", Runner: runner));

        Assert.Same(runner, registry.GetSkillRunner("runnable"));

        handle.Dispose();

        Assert.Null(registry.GetSkillRunner("runnable"));
        Assert.Empty(registry.Skills);
    }

    [Fact]
    public void RegisterSkillProviderKeysByNameAndSupportsOverride()
    {
        var registry = new ExtensionRegistry();
        var first = new FakeSkillProvider("alpha", 1);
        var replacement = new FakeSkillProvider("alpha", 9);

        registry.RegisterSkillProvider("extension:one", first);
        Assert.Single(registry.SkillProviders);
        Assert.Same(first, registry.SkillProviders[0].Value);

        registry.RegisterSkillProvider("extension:two", replacement, ExtensionOverridePolicy.Override);
        Assert.Single(registry.SkillProviders);
        Assert.Same(replacement, registry.SkillProviders[0].Value);
    }

    [Fact]
    public void RegisterSkillProviderRejectsDuplicateNameWithoutOverride()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkillProvider("extension:one", new FakeSkillProvider("alpha", 1));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterSkillProvider("extension:two", new FakeSkillProvider("alpha", 2)));
        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverSkillProvidersMergesRegisteredAndProviderSkills()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("registered", "Registered", "body", "/repo/registered/SKILL.md"));
        registry.RegisterSkillProvider("extension:provider", new FakeSkillProvider("provider", 3, Skills:
        [
            new ExtensionSkillDefinition("provided", "Provided", "body", "/repo/provided/SKILL.md")
        ]));

        var discovered = await registry.DiscoverSkillProvidersAsync();

        Assert.Equal(["provided", "registered"], discovered.Select(skill => skill.Name).OrderBy(name => name));
        var provided = discovered.Single(skill => skill.Name == "provided");
        Assert.Equal("provider", provided.Source);
        Assert.Equal(3, provided.SourcePriority);
    }

    [Fact]
    public async Task ProviderWithHigherSourcePriorityWinsNameCollision()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkillProvider("extension:low", new FakeSkillProvider("low", 2, Skills:
        [
            new ExtensionSkillDefinition("shared", "Low", "low-body", "/repo/low/SKILL.md")
        ]));
        registry.RegisterSkillProvider("extension:high", new FakeSkillProvider("high", 9, Skills:
        [
            new ExtensionSkillDefinition("shared", "High", "high-body", "/repo/high/SKILL.md")
        ]));

        var discovered = await registry.DiscoverSkillProvidersAsync();

        var winner = Assert.Single(discovered);
        Assert.Equal("shared", winner.Name);
        Assert.Equal("high-body", winner.Content);
        Assert.Equal("high", winner.Source);
        Assert.Equal(9, winner.SourcePriority);
    }

    [Fact]
    public async Task RegisteredExtensionSkillWinsOverLowerPriorityProviderSkillAndLosesToHigher()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "shared", "Extension", "extension-body", "/repo/shared/SKILL.md", Source: "extension:test", SourcePriority: 5));

        registry.RegisterSkillProvider("extension:provider", new FakeSkillProvider("provider", 2, Skills:
        [
            new ExtensionSkillDefinition("shared", "Provider", "provider-body", "/repo/provider/SKILL.md")
        ]));

        var first = await registry.DiscoverSkillProvidersAsync();
        Assert.Equal("extension-body", Assert.Single(first).Content);

        registry.RegisterSkillProvider("extension:other", new FakeSkillProvider("other", 9, Skills:
        [
            new ExtensionSkillDefinition("shared", "Other", "other-body", "/repo/other/SKILL.md")
        ]));

        var second = await registry.DiscoverSkillProvidersAsync();
        Assert.Equal("other-body", Assert.Single(second).Content);
    }

    [Fact]
    public async Task SkillProviderSkillKeepsExplicitSourceAndPriorityOverrides()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkillProvider("extension:provider", new FakeSkillProvider("provider", 3, Skills:
        [
            new ExtensionSkillDefinition("explicit", "Explicit", "body", "/repo/explicit/SKILL.md", Source: "custom", SourcePriority: 42)
        ]));

        var discovered = await registry.DiscoverSkillProvidersAsync();

        var skill = Assert.Single(discovered);
        Assert.Equal("custom", skill.Source);
        Assert.Equal(42, skill.SourcePriority);
    }

    [Fact]
    public void UnregisterBySourceRemovesSkillProvidersAndRunners()
    {
        var registry = new ExtensionRegistry();
        ExtensionSkillRunner runner = (_, _) => Task.FromResult(new ExtensionSkillRunResult("ran"));
        registry.RegisterSkillProvider("extension:source", new FakeSkillProvider("alpha", 1));
        registry.RegisterSkill("extension:source", new ExtensionSkillDefinition("run", "Run", "body", "/repo/run/SKILL.md", Runner: runner));

        registry.UnregisterBySource("extension:source");

        Assert.Empty(registry.SkillProviders);
        Assert.Empty(registry.Skills);
        Assert.Null(registry.GetSkillRunner("run"));
    }

    private sealed class FakeSkillProvider(string name, int priority, IReadOnlyList<ExtensionSkillDefinition>? Skills = null) : ISkillProvider
    {
        public string Name { get; } = name;
        public int Priority { get; } = priority;
        public Task<IReadOnlyList<ExtensionSkillDefinition>> DiscoverAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>(Skills ?? []);
    }
}
