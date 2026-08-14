using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai.Models;
using PiSharp.Extensions;
using PiSharp.PlanMode;
using PiSharp.PlanMode.Tests.Fakes;
using Xunit;

namespace PiSharp.PlanMode.Tests;

public sealed class PlanModeExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-plan-mode-extension", Guid.NewGuid().ToString("N"));

    public PlanModeExtensionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ModelRegistry.UnregisterBySource(ModelRegistry.DynamicSourceId);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static string MessageText(AgentMessage message)
        => message is UserMessage user
            ? string.Concat(user.Content.OfType<TextContent>().Select(content => content.Text))
            : string.Empty;


    private static async Task<PlanModeTestApi> CreateApiAsync(string? planFilesDir = null)
    {
        var api = new PlanModeTestApi
        {
            Cwd = "C:/project",
            SessionName = "session-abcdefgh",
            CurrentModel = new ModelDescriptor("test", "original-model", "api"),
            RegisteredToolNames = ["read", "grep", "find", "ls", "edit", "write", "bash"]
        };
        // The documented restrictedTools default (plan §9); the plugin would otherwise
        // fall back to a single comma-joined string, which intersects to nothing.
        await api.Settings.SetAsync("restrictedTools", new[] { "read", "grep", "find", "ls" });
        if (planFilesDir is not null)
            await api.Settings.SetAsync("planFilesDir", planFilesDir);
        return api;
    }

    [Fact]
    public async Task Initialize_RegistersFlagsCommandContributorAndHandlers()
    {
        var api = await CreateApiAsync(_root);
        var extension = new PlanModeExtension();

        await extension.InitializeAsync(api);

        var planFlag = Assert.Single(api.RegisteredFlags, flag => flag.Name == PlanModeExtension.PlanFlag);
        Assert.Equal(ExtensionFlagType.Boolean, planFlag.Type);
        var planModelFlag = Assert.Single(api.RegisteredFlags, flag => flag.Name == PlanModeExtension.PlanModelFlag);
        Assert.Equal(ExtensionFlagType.String, planModelFlag.Type);

        Assert.Single(api.RegisteredCommands, command => command.Name == "plan");
        Assert.Single(api.RegisteredPromptContributors);

        Assert.Contains(api.RegisteredHandlers, handler => handler.EventName == ExtensionEventNames.AgentEnd);
        Assert.Contains(api.RegisteredHandlers, handler => handler.EventName == ExtensionEventNames.Input);
        Assert.Contains(api.RegisteredHandlers, handler => handler.EventName == ExtensionEventNames.SessionStart);
        Assert.Contains(api.RegisteredHandlers, handler => handler.EventName == ExtensionEventNames.ResourcesDiscover);
        Assert.NotNull(extension.Service);
        Assert.Equal(PlanModePhase.Inactive, extension.Service!.Phase);
    }

    [Fact]
    public async Task SessionStart_WithPlanFlag_EntersPlanning()
    {
        var api = await CreateApiAsync(_root);
        api.FlagValues[PlanModeExtension.PlanFlag] = true;
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);

        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));

        var service = extension.Service!;
        Assert.Equal(PlanModePhase.Planning, service.Phase);
        Assert.Equal(["read", "grep", "find", "ls"], service.State.RestrictedToolNames);
        Assert.StartsWith(_root, service.PlanFile, StringComparison.Ordinal);
        Assert.Single(api.ClientEvents);
        Assert.Empty(api.SentMessages); // entering successfully sends no error message
    }

    [Fact]
    public async Task SessionStart_WithPlanModeEnabledSetting_EntersPlanning()
    {
        var api = await CreateApiAsync(_root);
        await api.Settings.SetAsync("planModeEnabled", true);
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);

        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));

        Assert.Equal(PlanModePhase.Planning, extension.Service!.Phase);
    }

    [Fact]
    public async Task SessionStart_WithoutFlagOrSetting_StaysInactive()
    {
        var api = await CreateApiAsync(_root);
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);

        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));

        Assert.Equal(PlanModePhase.Inactive, extension.Service!.Phase);
        Assert.Empty(api.ClientEvents);
        Assert.Empty(api.SetActiveToolsCalls);
    }

    [Fact]
    public async Task SessionStart_PlanModelFlag_OverridesPlanningModelSetting()
    {
        ModelRegistry.AddModel(new CatalogModel("test", "flag-model", new ModelDescriptor("test", "flag-model", "api")));
        ModelRegistry.AddModel(new CatalogModel("test", "setting-model", new ModelDescriptor("test", "setting-model", "api")));
        var api = await CreateApiAsync(_root);
        await api.Settings.SetAsync("planModeEnabled", true);
        await api.Settings.SetAsync("planningModel", "test/setting-model");
        api.FlagValues[PlanModeExtension.PlanModelFlag] = "test/flag-model";
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);

        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));

        Assert.Equal(PlanModePhase.Planning, extension.Service!.Phase);
        Assert.Equal("flag-model", extension.Service!.State.PlanningModel);
        Assert.Equal("flag-model", Assert.Single(api.SetModelCalls).Id);
    }

    [Fact]
    public async Task PlanCommand_Enter_WhenInactive_StartsPlanning()
    {
        var api = await CreateApiAsync(_root);
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);
        var command = Assert.Single(api.RegisteredCommands);

        await command.Handler("", CancellationToken.None);

        Assert.Equal(PlanModePhase.Planning, extension.Service!.Phase);
    }

    [Fact]
    public async Task PlanCommand_Status_WhenAlreadyActive_PrintsState()
    {
        var api = await CreateApiAsync(_root);
        api.FlagValues[PlanModeExtension.PlanFlag] = true;
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);
        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));
        var command = Assert.Single(api.RegisteredCommands);

        await command.Handler("", CancellationToken.None);

        var reply = Assert.Single(api.SentMessages);
        Assert.Contains("phase=planning", MessageText(reply), StringComparison.Ordinal);
        Assert.Contains("planFile=", MessageText(reply), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllSubscriptions()
    {
        var api = await CreateApiAsync(_root);
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);
        Assert.Equal(0, api.DisposedSubscriptions);

        await extension.DisposeAsync();

        // 2 flags + 1 prompt contributor + 4 event handlers + 1 command registration.
        Assert.Equal(8, api.DisposedSubscriptions);
    }

    [Fact]
    public async Task PlanCommand_Approve_WithoutCapturedBody_ReportsError()
    {
        var api = await CreateApiAsync(_root);
        api.FlagValues[PlanModeExtension.PlanFlag] = true;
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);
        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));
        var command = Assert.Single(api.RegisteredCommands);

        await command.Handler("approve", CancellationToken.None);

        var reply = Assert.Single(api.SentMessages);
        Assert.Contains("Cannot approve a plan: no plan body has been captured yet.", MessageText(reply), StringComparison.Ordinal);
        Assert.Equal(PlanModePhase.Planning, extension.Service!.Phase);
    }

    [Fact]
    public async Task PlanCommand_FullLifecycle_ApproveAndEnd()
    {
        var api = await CreateApiAsync(_root);
        api.FlagValues[PlanModeExtension.PlanFlag] = true;
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);
        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));
        var command = Assert.Single(api.RegisteredCommands);
        var service = extension.Service!;

        await service.CapturePlanAsync("the plan body");
        await command.Handler("approve", CancellationToken.None);

        Assert.Equal(PlanModePhase.Executing, service.Phase);
        Assert.Contains("Plan approved.", MessageText(Assert.Single(api.SentMessages)), StringComparison.Ordinal);
        Assert.Equal(PlanFileStatus.Approved, (await new PlanFileStore(_root).ReadAsync(service.PlanFile)).Status);

        api.SentMessages.Clear();
        await command.Handler("end", CancellationToken.None);

        Assert.Equal(PlanModePhase.Inactive, service.Phase);
        Assert.Contains("Plan mode ended.", MessageText(Assert.Single(api.SentMessages)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanCommand_UnknownArgs_PrintsUsage()
    {
        var api = await CreateApiAsync(_root);
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);
        var command = Assert.Single(api.RegisteredCommands);

        await command.Handler("sideways", CancellationToken.None);

        Assert.Contains("Usage: /plan", MessageText(Assert.Single(api.SentMessages)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResourcesDiscover_ReloadsSettings()
    {
        var api = await CreateApiAsync(_root);
        var extension = new PlanModeExtension();
        await extension.InitializeAsync(api);

        await api.Settings.SetAsync("planModeEnabled", true);
        await api.RaiseAsync(ExtensionEventNames.ResourcesDiscover, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));

        // planModeEnabled is now true after the reload; session start must enter planning.
        await api.RaiseAsync(ExtensionEventNames.SessionStart, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("test")), new AgentHarnessOwnEvent.SessionStart("test"));

        Assert.Equal(PlanModePhase.Planning, extension.Service!.Phase);
    }

}
