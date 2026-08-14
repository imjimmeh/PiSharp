using System.Text.Json.Nodes;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.ModelRoles.Tests;

public class ModelRolesExtensionTests : IDisposable
{
    public void Dispose() => ModelRoleRegistry.Clear();

    private static async Task<TestExtensionApi> CreateApiAsync(string modelRolesJson)
    {
        var api = new TestExtensionApi();
        if (modelRolesJson.Length > 0)
        {
            await api.SettingsImpl.SetAsync("modelRoles", JsonNode.Parse(modelRolesJson));
        }
        return api;
    }

    private static async Task RaiseSettingsChangedAsync(TestExtensionApi api, string key)
        => await api.RaiseAsync(ExtensionEventNames.SettingsChanged, null!, new ExtensionSettingsChange(key, null, "Fake", "fake"));

    [Fact]
    public async Task InitializeAsync_registers_resolver_and_slash_commands()
    {
        ModelRoleRegistry.Clear();
        var api = await CreateApiAsync("""{ "@fast_worker": "anthropic/claude-haiku-4-5", "@smol": ["openai-codex/gpt-5.4-mini"] }""");
        var extension = new ModelRolesExtension();

        await extension.InitializeAsync(api);

        var resolution = ModelRoleRegistry.Resolve("fast_worker");
        Assert.NotNull(resolution);
        Assert.Equal(new[] { "anthropic/claude-haiku-4-5" }, resolution!.Selectors);
        Assert.NotNull(ModelRoleRegistry.Resolve("smol"));
        Assert.Contains(api.Commands, c => c.Name == "roles");
        Assert.Contains(api.Commands, c => c.Name == "model-roles");
        Assert.Equal(new[] { "fast_worker", "smol" }, ModelRoleRegistry.Roles);
    }

    [Fact]
    public async Task InitializeAsync_without_settings_registers_empty_resolver()
    {
        ModelRoleRegistry.Clear();
        var api = await CreateApiAsync("");
        var extension = new ModelRolesExtension();

        await extension.InitializeAsync(api);

        Assert.Empty(ModelRoleRegistry.Roles);
        Assert.Null(ModelRoleRegistry.Resolve("fast_worker"));
    }

    [Fact]
    public async Task SettingsChanged_rebuilds_roles_added_and_removed()
    {
        ModelRoleRegistry.Clear();
        var api = await CreateApiAsync("""{ "@fast_worker": "anthropic/claude-haiku-4-5" }""");
        var extension = new ModelRolesExtension();
        await extension.InitializeAsync(api);
        Assert.NotNull(ModelRoleRegistry.Resolve("fast_worker"));

        await api.SettingsImpl.SetAsync("modelRoles", JsonNode.Parse("""{ "@smol": ["openai-codex/gpt-5.4-mini"] }"""));
        await RaiseSettingsChangedAsync(api, "modelRoles");

        Assert.Null(ModelRoleRegistry.Resolve("fast_worker"));
        Assert.NotNull(ModelRoleRegistry.Resolve("smol"));
    }

    [Fact]
    public async Task SettingsChanged_rebuilds_effort_presets()
    {
        ModelRoleRegistry.Clear();
        var api = new TestExtensionApi();
        await api.SettingsImpl.SetAsync("modelRoles", JsonNode.Parse("""{ "@review": { "models": "anthropic/claude-sonnet-4-5:high", "effort": "review" } }"""));
        await api.SettingsImpl.SetAsync("effort", JsonNode.Parse("""{ "review": { "thinkingLevel": "high" } }"""));
        var extension = new ModelRolesExtension();
        await extension.InitializeAsync(api);

        Assert.Equal(ThinkingLevel.High, ModelRoleRegistry.Resolve("review")!.Effort!.ThinkingLevel);

        await api.SettingsImpl.SetAsync("effort", JsonNode.Parse("""{ "review": { "thinkingLevel": "medium" } }"""));
        await RaiseSettingsChangedAsync(api, "effort");

        Assert.Equal(ThinkingLevel.Medium, ModelRoleRegistry.Resolve("review")!.Effort!.ThinkingLevel);
    }

    [Fact]
    public async Task SettingsChanged_for_unrelated_key_does_not_rebuild()
    {
        ModelRoleRegistry.Clear();
        var api = await CreateApiAsync("""{ "@fast_worker": "anthropic/claude-haiku-4-5" }""");
        var extension = new ModelRolesExtension();
        await extension.InitializeAsync(api);

        await api.SettingsImpl.SetAsync("modelRoles", JsonNode.Parse("""{ "@smol": ["openai-codex/gpt-5.4-mini"] }"""));
        await RaiseSettingsChangedAsync(api, "defaultProvider");

        Assert.NotNull(ModelRoleRegistry.Resolve("fast_worker"));
        Assert.Null(ModelRoleRegistry.Resolve("smol"));
    }

    [Fact]
    public async Task DisposeAsync_unregisters_resolver_and_disposes_subscriptions()
    {
        ModelRoleRegistry.Clear();
        var api = await CreateApiAsync("""{ "@fast_worker": "anthropic/claude-haiku-4-5" }""");
        var extension = new ModelRolesExtension();
        await extension.InitializeAsync(api);
        Assert.NotNull(ModelRoleRegistry.Resolve("fast_worker"));

        await extension.DisposeAsync();

        Assert.Null(ModelRoleRegistry.Resolve("fast_worker"));
        Assert.Empty(ModelRoleRegistry.Roles);

        // The settings subscription is gone: a change no longer rebuilds the registry.
        await api.SettingsImpl.SetAsync("modelRoles", JsonNode.Parse("""{ "@smol": ["openai-codex/gpt-5.4-mini"] }"""));
        await RaiseSettingsChangedAsync(api, "modelRoles");
        Assert.Null(ModelRoleRegistry.Resolve("smol"));
    }

    [Fact]
    public async Task RolesCommand_lists_roles_selectors_and_effort()
    {
        var api = new TestExtensionApi();
        await api.SettingsImpl.SetAsync("modelRoles", JsonNode.Parse("""
            { "@fast_worker": "anthropic/claude-haiku-4-5",
              "@review": { "models": ["anthropic/claude-sonnet-4-5:high", "anthropic/claude-opus-4-5"], "effort": "review" } }
            """));
        await api.SettingsImpl.SetAsync("effort", JsonNode.Parse("""{ "review": { "thinkingLevel": "high", "budgets": { "high": 24000 } } }"""));
        var extension = new ModelRolesExtension();
        await extension.InitializeAsync(api);

        var command = Assert.Single(api.Commands, c => c.Name == "roles");
        await command.InvokeAsync(new ExtensionCommandContext(
            command.Name,
            string.Empty,
            NoExtensionUi.Instance,
            null!,
            null!,
            null!,
            new Dictionary<string, object?>(),
            CancellationToken.None));

        var text = SentText(api);
        Assert.Contains("@fast_worker -> anthropic/claude-haiku-4-5", text);
        Assert.Contains("@review -> anthropic/claude-sonnet-4-5:high, anthropic/claude-opus-4-5", text);
        Assert.Contains("effort: thinking=high", text);
        Assert.Contains("budgets={high=24000}", text);
    }

    [Fact]
    public async Task RolesCommand_without_roles_prints_guidance()
    {
        var api = await CreateApiAsync("");
        var extension = new ModelRolesExtension();
        await extension.InitializeAsync(api);

        var command = Assert.Single(api.Commands, c => c.Name == "roles");
        await command.InvokeAsync(new ExtensionCommandContext(
            command.Name,
            string.Empty,
            NoExtensionUi.Instance,
            null!,
            null!,
            null!,
            new Dictionary<string, object?>(),
            CancellationToken.None));

        Assert.Contains("No model roles configured", SentText(api));
    }

    [Fact]
    public async Task Invalid_entries_collect_diagnostics_and_do_not_block_valid_roles()
    {
        ModelRoleRegistry.Clear();
        var api = await CreateApiAsync("""{ "@good": "anthropic/claude-haiku-4-5", "@bad": 42 }""");
        var extension = new ModelRolesExtension();
        await extension.InitializeAsync(api);

        Assert.NotNull(ModelRoleRegistry.Resolve("good"));
        Assert.Contains(extension.Diagnostics, d => d.Contains("Skipping model role '@bad'", StringComparison.Ordinal));
    }

    private static string SentText(TestExtensionApi api)
    {
        var message = Assert.Single(api.SentMessages);
        var user = Assert.IsType<UserMessage>(message);
        return string.Concat(user.Content.OfType<TextContent>().Select(t => t.Text));
    }
}
