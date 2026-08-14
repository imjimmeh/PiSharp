using PiSharp.ContinualHarness;
using Xunit;

namespace PiSharp.ContinualHarness.Tests;

public sealed class ContinualHarnessExtensionTests
{
    [Fact]
    public async Task Disabled_By_Settings_Registers_Nothing()
    {
        var api = new StubApi();
        ((StubSettingsApi)api.Settings).Values["enabled"] = false;

        await new ContinualHarnessExtension().InitializeAsync(api);

        Assert.Empty(api.Commands);
        Assert.Empty(api.RegisteredTools);
        Assert.Empty(api.Flags);
    }

    [Fact]
    public async Task Enabled_Registers_Command_Flag_Tool_Contributor()
    {
        var api = new StubApi();
        await new ContinualHarnessExtension().InitializeAsync(api);

        Assert.Contains(api.Commands, c => c.Name == "refine");
        Assert.Contains(api.Flags, f => f.Name == "refine");
        Assert.Contains(api.RegisteredTools, t => t.Name == "refine");
        Assert.Single(((StubPromptApi)api.Prompt).Contributors);
    }

    [Fact]
    public async Task Tool_Not_Registered_When_ToolDisabled()
    {
        var api = new StubApi();
        ((StubSettingsApi)api.Settings).Values["toolEnabled"] = false;

        await new ContinualHarnessExtension().InitializeAsync(api);

        Assert.Contains(api.Commands, c => c.Name == "refine");
        Assert.Empty(api.RegisteredTools);
    }
}
