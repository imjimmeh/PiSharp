using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;
using PiSharp.Permissions;
using PiSharp.Permissions.Tests.Fakes;
using Xunit;

namespace PiSharp.Permissions.Tests;

public sealed class PermissionsExtensionTests
{
    private readonly PermissionsTestApi _api;

    public PermissionsExtensionTests()
    {
        _api = new PermissionsTestApi
        {
            Cwd = "C:/project",
            SessionName = "session-abcdefgh",
            HasUi = false,
            ExecutionEnv = new FakeExecutionEnv { Cwd = "C:/project" }
        };
    }

    private async Task<PermissionsExtension> InitializeAsync()
    {
        var extension = new PermissionsExtension();
        await extension.InitializeAsync(_api);
        return extension;
    }
    private static string MessageText(AgentMessage message)
        => message is UserMessage user
            ? string.Concat(user.Content.OfType<TextContent>().Select(content => content.Text))
            : string.Empty;

    private static ExtensionCommandRegistration PermissionsCommand(PermissionsTestApi api)
        => Assert.Single(api.RegisteredCommands, c => c.Name == "permissions");

    private static Task InvokeSlashAsync(PermissionsTestApi api, string args)
        => PermissionsCommand(api).Handler(args, CancellationToken.None);

    // --- Phase 4: registration + live reload ---

    [Fact]
    public async Task Initialize_RegistersMiddlewareCommandAndSettingsSubscription()
    {
        var extension = await InitializeAsync();

        Assert.Single(_api.RegisteredMiddlewares);
        var command = Assert.Single(_api.RegisteredCommands, c => c.Name == "permissions");
        Assert.Contains("grant", command.Description);

        // A settings change after init triggers a live policy reload.
        Assert.Equal(PermissionsPolicy.ModePrompt, extension.Policy.Mode);
        await _api.Settings.SetAsync("mode", PermissionsPolicy.ModeStrict);
        Assert.True(extension.Policy.IsStrict);
        Assert.Empty(extension.Policy.AllowRules);
    }

    [Fact]
    public async Task SettingsChange_ReloadsRules()
    {
        var extension = await InitializeAsync();

        await _api.Settings.SetAsync("allow", new List<PermissionRule> { new("bash") });

        Assert.Single(extension.Policy.AllowRules);
        Assert.Equal("bash", extension.Policy.AllowRules[0].Tool);
    }

    [Fact]
    public async Task MalformedSettingsChange_KeepsLastGoodPolicy()
    {
        var extension = await InitializeAsync();

        await _api.Settings.SetAsync("deny", new List<PermissionRule> { new("bash", "([invalid") });

        Assert.Empty(extension.Policy.DenyRules); // still the default policy
    }

    [Fact]
    public async Task DisposeAsync_DisposesSubscriptions()
    {
        var extension = await InitializeAsync();
        var before = _api.DisposedSubscriptions;

        await extension.DisposeAsync();

        Assert.True(_api.DisposedSubscriptions > before);
    }

    // --- Phase 4: slash command ---

    [Fact]
    public async Task Slash_GrantAndRevoke_RoundTripThroughGrantStore()
    {
        await InitializeAsync();

        await InvokeSlashAsync(_api, "grant bash");
        var grant = await new GrantStore(_api.State).FindAsync("bash", "session-abcdefgh");
        Assert.NotNull(grant);
        Assert.Equal(PermissionAction.Allow, grant!.Action);

        await InvokeSlashAsync(_api, "revoke bash");
        Assert.Null(await new GrantStore(_api.State).FindAsync("bash", "session-abcdefgh"));
    }

    [Fact]
    public async Task Slash_Reset_ClearsGrants()
    {
        await InitializeAsync();

        await InvokeSlashAsync(_api, "grant bash");
        await InvokeSlashAsync(_api, "grant write");
        Assert.Equal(2, (await new GrantStore(_api.State).ListAsync()).Count);

        await InvokeSlashAsync(_api, "reset");

        Assert.Empty(await new GrantStore(_api.State).ListAsync());
        Assert.Contains(_api.SentMessages, m => MessageText(m).Contains("cleared"));
    }

    [Fact]
    public async Task Slash_List_ShowsPolicyAndGrants()
    {
        await _api.Settings.SetAsync("deny", new List<PermissionRule> { new("bash", "git push.*") });
        await InitializeAsync();
        await InvokeSlashAsync(_api, "grant bash");

        await InvokeSlashAsync(_api, "list");

        var text = string.Join("\n", _api.SentMessages.Select(MessageText));
        Assert.Contains("mode=prompt", text);
        Assert.Contains("allow:bash@session-abcdefgh", text);
        Assert.Contains("session-abcdefgh", text);
    }

    [Fact]
    public async Task Slash_Status_ReportsModeAndHeadlessPosture()
    {
        await InitializeAsync();

        await InvokeSlashAsync(_api, "status");

        var text = string.Join("\n", _api.SentMessages.Select(MessageText));
        Assert.Contains("mode=prompt", text);
        Assert.Contains("headless", text);
    }

    [Fact]
    public async Task Slash_UnknownSubcommand_PrintsUsage()
    {
        await InitializeAsync();

        await InvokeSlashAsync(_api, "frobnicate");

        Assert.Contains(_api.SentMessages, m => MessageText(m).Contains("Usage: /permissions"));
    }
}
