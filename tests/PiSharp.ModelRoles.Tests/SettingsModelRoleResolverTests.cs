using System.Text.Json;
using PiSharp.Agent.Core.Models;
using Xunit;

namespace PiSharp.ModelRoles.Tests;

public class SettingsModelRoleResolverTests : IDisposable
{
    private static readonly JsonElement RolesSection = JsonDocument.Parse("""
        { "@fast_worker": "anthropic/claude-haiku-4-5",
          "@smol": ["openai-codex/gpt-5.4-mini", "anthropic/claude-haiku-4-5"] }
        """).RootElement;

    private static SettingsModelRoleResolver CreateResolver()
    {
        var roles = ModelRolesSettingsParser.Parse(RolesSection, effort: null);
        return new SettingsModelRoleResolver("test-source", roles);
    }

    public void Dispose() => ModelRoleRegistry.Clear();

    [Fact]
    public void Resolve_returns_resolution_for_known_role()
    {
        var resolver = CreateResolver();

        var resolution = resolver.Resolve("fast_worker");

        Assert.NotNull(resolution);
        Assert.Equal("fast_worker", resolution!.Role);
        Assert.Equal(new[] { "anthropic/claude-haiku-4-5" }, resolution.Selectors);
    }

    [Fact]
    public void Resolve_accepts_at_prefix_and_any_case()
    {
        var resolver = CreateResolver();

        Assert.NotNull(resolver.Resolve("@fast_worker"));
        Assert.NotNull(resolver.Resolve("FAST_WORKER"));
        Assert.NotNull(resolver.Resolve("@Smol"));
    }

    [Fact]
    public void Resolve_unknown_role_returns_null()
    {
        var resolver = CreateResolver();
        Assert.Null(resolver.Resolve("does_not_exist"));
        Assert.Null(resolver.Resolve("@does_not_exist"));
    }

    [Fact]
    public void Roles_lists_normalized_names()
    {
        var resolver = CreateResolver();
        Assert.Equal(new[] { "fast_worker", "smol" }, resolver.Roles);
    }

    [Fact]
    public void SourceId_is_exposed_for_registry_bookkeeping()
    {
        var resolver = CreateResolver();
        Assert.Equal("test-source", resolver.SourceId);
    }

    [Fact]
    public void Registry_round_trip_registers_and_resolves()
    {
        var resolver = CreateResolver();
        ModelRoleRegistry.Clear();
        ModelRoleRegistry.Register(resolver);

        var resolution = ModelRoleRegistry.Resolve("smol");

        Assert.NotNull(resolution);
        Assert.Equal("smol", resolution!.Role);
        Assert.Equal(new[] { "openai-codex/gpt-5.4-mini", "anthropic/claude-haiku-4-5" }, resolution.Selectors);
        Assert.Equal(new[] { "fast_worker", "smol" }, ModelRoleRegistry.Roles);
    }

    [Fact]
    public void Registry_first_wins_when_two_resolvers_define_the_same_role()
    {
        ModelRoleRegistry.Clear();
        var first = new SettingsModelRoleResolver("first", new Dictionary<string, ModelRoleResolution>
        {
            ["shared"] = new("shared", ["anthropic/claude-haiku-4-5"], null)
        });
        var second = new SettingsModelRoleResolver("second", new Dictionary<string, ModelRoleResolution>
        {
            ["shared"] = new("shared", ["openai-codex/gpt-5.4-mini"], null)
        });

        ModelRoleRegistry.Register(first);
        ModelRoleRegistry.Register(second);

        var resolution = ModelRoleRegistry.Resolve("shared");
        Assert.Equal("anthropic/claude-haiku-4-5", Assert.Single(resolution!.Selectors));
    }

    [Fact]
    public void Registry_unregister_removes_only_that_source()
    {
        ModelRoleRegistry.Clear();
        var first = CreateResolver();
        var second = new SettingsModelRoleResolver("other", new Dictionary<string, ModelRoleResolution>
        {
            ["extra"] = new("extra", ["openai-codex/gpt-5.4-mini"], null)
        });
        ModelRoleRegistry.Register(first);
        ModelRoleRegistry.Register(second);

        Assert.True(ModelRoleRegistry.Unregister("test-source"));

        Assert.Null(ModelRoleRegistry.Resolve("fast_worker"));
        Assert.NotNull(ModelRoleRegistry.Resolve("extra"));
        Assert.False(ModelRoleRegistry.Unregister("test-source"));
    }
}
