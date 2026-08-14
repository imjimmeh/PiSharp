using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class RulesExtensionTests
{
    [Fact]
    public async Task Initialize_WiresFlagsProvidersEventsPromptAndEngine()
    {
        using var home = new TempDir();
        var api = new TestFakeApi();
        RulesEngine? intercepted = null;
        var extension = new RulesExtension(home.Root, engine => intercepted = engine);

        await extension.InitializeAsync(api);

        Assert.False((bool)api.GetFlag(RulesExtension.NoRulesFlag)!);
        Assert.False((bool)api.GetFlag(RulesExtension.DisableStickyFlag)!);

        // Both built-in providers registered.
        Assert.Contains(RulesDirectoryProvider.ProviderName, api.Rules.GetProviderNames());
        Assert.Contains(StickyRulesProvider.ProviderName, api.Rules.GetProviderNames());

        // Prompt contributor + the three discovery triggers registered.
        Assert.Single(api.RegisteredContributors);
        var names = api.RegisteredHandlers.Select(h => h.EventName).ToArray();
        Assert.Contains(ExtensionEventNames.ResourcesDiscover, names);
        Assert.Contains(ExtensionEventNames.ResourcesUpdate, names);
        Assert.Contains(ExtensionEventNames.SessionStart, names);

        // Engine created, not disabled, and handed to the interceptor seam.
        Assert.NotNull(extension.Engine);
        Assert.False(extension.Engine!.Disabled);
        Assert.Same(extension.Engine, intercepted);
    }

    [Fact]
    public async Task NoRulesFlag_RegistersNoProvidersAndDisablesEngine()
    {
        using var home = new TempDir();
        var api = new TestFakeApi();
        api.SetFlag(RulesExtension.NoRulesFlag, true);
        var extension = new RulesExtension(home.Root);

        await extension.InitializeAsync(api);

        Assert.Empty(api.Rules.GetProviderNames());
        Assert.NotNull(extension.Engine);
        Assert.True(extension.Engine!.Disabled);
        // Disabled discovery yields nothing.
        Assert.Empty(await extension.Engine.GetRulesAsync());
    }

    [Fact]
    public async Task DisableStickyFlag_RegistersOnlyTheDirectoryProvider()
    {
        using var home = new TempDir();
        var api = new TestFakeApi();
        api.SetFlag(RulesExtension.DisableStickyFlag, true);
        var extension = new RulesExtension(home.Root);

        await extension.InitializeAsync(api);

        var providerName = Assert.Single(api.Rules.GetProviderNames());
        Assert.Equal(RulesDirectoryProvider.ProviderName, providerName);
    }

    [Fact]
    public async Task RegisterInterceptor_EngineIsQueryableAsStreamDeltaInterceptor()
    {
        using var home = new TempDir();
        var registry = new ExtensionRegistry();
        RulesEngine? intercepted = null;
        var extension = new RulesExtension(home.Root, engine =>
        {
            intercepted = engine;
            registry.RegisterStreamDeltaInterceptor("pisharp-rules", engine);
        });

        await extension.InitializeAsync(new TestFakeApi());

        Assert.NotNull(extension.Engine);
        Assert.Same(extension.Engine, intercepted);
        var registration = Assert.Single(registry.StreamDeltaInterceptors);
        Assert.Equal("stream-delta:pisharp-rules", registration.Id);
        Assert.Same(extension.Engine, registration.Value);
    }


    [Fact]
    public async Task FireResourcesDiscover_DiscoversRuleFiles()
    {
        using var home = new TempDir();
        var projectDir = Path.Combine(home.Root, "repo");
        var rulesDir = Path.Combine(projectDir, ".pi", "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "no-todo.md"),
            "---\npattern: (?i)todo list\n---\nDo not add todos.");

        var api = new TestFakeApi { Cwd = projectDir };
        var extension = new RulesExtension(home.Root);
        await extension.InitializeAsync(api);

        // Fire resources_discover through the registered handler.
        var handler = api.RegisteredHandlers.First(h => h.EventName == ExtensionEventNames.ResourcesDiscover).Handler;
        await handler(new ExtensionEvent(ExtensionEventNames.ResourcesDiscover, null!), CancellationToken.None);

        var rules = await extension.Engine!.GetRulesAsync();
        var rule = Assert.Single(rules);
        Assert.Equal("no-todo", rule.Name);
        Assert.Equal("(?i)todo list", rule.TriggerPattern);
    }

    [Fact]
    public void ComputeRuleRoots_NearestFirstThenUserDir()
    {
        using var home = new TempDir();
        var project = Path.Combine(home.Root, "repo");
        var nested = Path.Combine(project, "src");
        Directory.CreateDirectory(Path.Combine(project, ".pi", "rules"));
        Directory.CreateDirectory(Path.Combine(nested, ".pi", "rules"));
        var userRules = Path.Combine(home.Root, ".pi", "agent", "rules");
        Directory.CreateDirectory(userRules);

        var roots = RulesExtension.ComputeRuleRoots(nested, userRules);

        Assert.Equal(3, roots.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(nested, ".pi", "rules")), roots[0]);
        Assert.Equal(Path.GetFullPath(Path.Combine(project, ".pi", "rules")), roots[1]);
        Assert.Equal(Path.GetFullPath(userRules), roots[2]);
    }
}
