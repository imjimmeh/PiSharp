using System.Reflection;
using System.Text.Json;
using PiSharp.Agent.Resources;
using PiSharp.Extensions;
using PiSharp.InternalUrls.Services;
using Xunit;

namespace PiSharp.InternalUrls.Tests;

/// <summary>
/// Extension-level coverage: the assembly carries the <see cref="ExtensionMetadataAttribute"/>
/// id per plan §7, <c>InitializeAsync</c> seeds <c>skill</c>/<c>agent</c>/<c>diff</c>
/// into the runtime-wide registry through <see cref="IExtensionApi.Urls"/>, and
/// the default skill accessor snapshots extension-registered skills.
/// </summary>
public sealed class InternalUrlsExtensionTests
{
    [Fact]
    public void AssemblyCarriesExtensionMetadata()
    {
        var attribute = typeof(InternalUrlsExtension).Assembly.GetCustomAttribute<ExtensionMetadataAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("pisharp-internal-urls", attribute!.Id);
        Assert.False(string.IsNullOrWhiteSpace(attribute.Name));
        Assert.False(string.IsNullOrWhiteSpace(attribute.Version));
    }

    [Fact]
    public async Task InitializeAsync_RegistersAllThreeSchemes()
    {
        var api = new FakeExtensionApi();
        var extension = new InternalUrlsExtension(
            skillLookup: _ => null,
            subagentResult: _ => null,
            diffLedger: new DiffLedger());

        await extension.InitializeAsync(api, CancellationToken.None);

        Assert.Equal(["agent", "diff", "skill"], api.Registry.Schemes);
    }

    [Fact]
    public async Task InitializeAsync_RegisteredSkillResolver_ResolvesSkillBody()
    {
        var api = new FakeExtensionApi();
        var extension = new InternalUrlsExtension(
            skillLookup: name => name == "web" ? new Skill("web", "Web skills", "body of web skill", @"C:\skills\web\SKILL.md") : null,
            subagentResult: _ => null,
            diffLedger: new DiffLedger());

        await extension.InitializeAsync(api, CancellationToken.None);

        Assert.True(api.Registry.TryGet("skill", out var resolver));
        var result = await resolver!.ResolveAsync(new InternalUrlRequest("skill", "web", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("body of web skill", result.Content);
    }

    [Fact]
    public async Task InitializeAsync_RegisteredAgentResolver_ResolvesField()
    {
        var api = new FakeExtensionApi();
        var resultJson = JsonSerializer.Deserialize<JsonElement>("""{ "summary": "done" }""");
        var extension = new InternalUrlsExtension(
            skillLookup: _ => null,
            subagentResult: id => id == "abc" ? resultJson : null,
            diffLedger: new DiffLedger());

        await extension.InitializeAsync(api, CancellationToken.None);

        Assert.True(api.Registry.TryGet("agent", out var resolver));
        var result = await resolver!.ResolveAsync(new InternalUrlRequest("agent", "abc/summary", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("\"done\"", result.Content);
    }

    [Fact]
    public async Task InitializeAsync_RegisteredDiffResolver_ReturnsRecordedDiff()
    {
        var api = new FakeExtensionApi();
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "the-unified-diff");
        var extension = new InternalUrlsExtension(
            skillLookup: _ => null,
            subagentResult: _ => null,
            diffLedger: ledger);

        await extension.InitializeAsync(api, CancellationToken.None);

        Assert.True(api.Registry.TryGet("diff", out var resolver));
        // No path normalizer is injected here, so the latest-diff form is used
        // to exercise the registered resolver end to end.
        var result = await resolver!.ResolveAsync(new InternalUrlRequest("diff", "", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Contains("the-unified-diff", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_DefaultSkillAccessor_ResolvesApiRegisteredSkills()
    {
        var api = new FakeExtensionApi();
        api.Skills.RegisterSkill(new ExtensionSkillRegistration(
            "web",
            "Web skills",
            "body from api",
            @"C:\skills\web\SKILL.md",
            DisableModelInvocation: false));
        var extension = new InternalUrlsExtension(); // no injected accessors

        await extension.InitializeAsync(api, CancellationToken.None);

        Assert.True(api.Registry.TryGet("skill", out var resolver));
        var result = await resolver!.ResolveAsync(new InternalUrlRequest("skill", "web", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("body from api", result.Content);
    }
}
