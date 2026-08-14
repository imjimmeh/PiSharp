using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent.Core;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.UiBridge;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Server.Tests;

/// <summary>
/// Daemon-side interception of theme UI kinds (plan C8 §7): <c>get_all_themes</c>/<c>get_theme</c>/
/// <c>set_theme</c> are answered from the <see cref="ThemeRegistry"/> by the bridge and never
/// forwarded to an attached client as <c>ui_request</c> events.
/// </summary>
public sealed class ServerUiBridgeThemeTests
{
    [Fact]
    public async Task GetAllThemes_AnsweredDaemonSide_WithoutUiRequest()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark", "tokens": { "background": "#1e1e1e" } }"""));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var themes = new ThemeRegistry();
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var bridge = new ServerUiBridge(registry, themeRegistry: themes);
        var live = await CreateSessionAsync(registry);

        var response = await bridge.RequestUiAsync(new ServerUiIntent("r1", "get_all_themes", "Themes", null, null, null));

        Assert.False(response.Cancelled);
        var items = Assert.IsAssignableFrom<IReadOnlyList<object>>(response.Value);
        Assert.Single(items);
        var item = ThemeRegistryTests.SerializeData(items[0]);
        Assert.Equal("Dark", item.GetProperty("name").GetString());
        Assert.Equal("#1e1e1e", item.GetProperty("document").GetProperty("tokens").GetProperty("background").GetString());
        Assert.DoesNotContain(live.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "ui_request");
    }

    [Fact]
    public async Task GetTheme_AnsweredDaemonSide_ReturnsActiveTheme()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var themes = new ThemeRegistry();
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        Assert.True(themes.TrySetActive("Dark"));
        var bridge = new ServerUiBridge(registry, themeRegistry: themes);
        var live = await CreateSessionAsync(registry);

        var response = await bridge.RequestUiAsync(new ServerUiIntent("r2", "get_theme", "Theme", null, null, null));

        Assert.False(response.Cancelled);
        var item = ThemeRegistryTests.SerializeData(response.Value);
        Assert.Equal("Dark", item.GetProperty("name").GetString());
        Assert.DoesNotContain(live.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "ui_request");
    }

    [Fact]
    public async Task GetTheme_WithoutActiveTheme_ReturnsNull()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry, themeRegistry: new ThemeRegistry());
        await CreateSessionAsync(registry);

        var response = await bridge.RequestUiAsync(new ServerUiIntent("r3", "get_theme", "Theme", null, null, null));

        Assert.False(response.Cancelled);
        Assert.Null(response.Value);
    }

    [Fact]
    public async Task SetTheme_AnsweredDaemonSide_ActivatesAndBroadcasts()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var themes = new ThemeRegistry();
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var bridge = new ServerUiBridge(registry, themeRegistry: themes);
        var live = await CreateSessionAsync(registry);
        using var payloadDoc = JsonDocument.Parse("""{ "theme": "Dark" }""");
        var payload = payloadDoc.RootElement;

        var response = await bridge.RequestUiAsync(new ServerUiIntent("r4", "set_theme", "Theme", null, null, payload));

        Assert.False(response.Cancelled);
        Assert.Equal("Dark", themes.ActiveName);
        Assert.Single(live.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "theme_changed");
        Assert.DoesNotContain(live.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "ui_request");
    }

    [Fact]
    public async Task SetTheme_UnknownName_ReturnsCancelled()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var themes = new ThemeRegistry();
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var bridge = new ServerUiBridge(registry, themeRegistry: themes);
        var live = await CreateSessionAsync(registry);
        using var payloadDoc = JsonDocument.Parse("""{ "theme": "Missing" }""");
        var payload = payloadDoc.RootElement;

        var response = await bridge.RequestUiAsync(new ServerUiIntent("r5", "set_theme", "Theme", null, null, payload));

        Assert.True(response.Cancelled);
        Assert.Null(themes.ActiveName);
        Assert.DoesNotContain(live.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "theme_changed");
    }

    [Fact]
    public async Task SetTheme_PlainNameInMessage_IsAccepted()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var themes = new ThemeRegistry();
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var bridge = new ServerUiBridge(registry, themeRegistry: themes);
        await CreateSessionAsync(registry);

        var response = await bridge.RequestUiAsync(new ServerUiIntent("r6", "set_theme", "Theme", "Dark", null, null));

        Assert.False(response.Cancelled);
        Assert.Equal("Dark", themes.ActiveName);
    }

    [Fact]
    public async Task NonThemeKind_StillRoundTripsAsUiRequest()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry, themeRegistry: new ThemeRegistry());
        var live = await CreateSessionAsync(registry);

        _ = bridge.RequestUiAsync(new ServerUiIntent("r7", "select", "Pick", "Choose", ["a"], null));

        await Task.Delay(50);
        Assert.Single(live.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "ui_request");
    }

    // --- shared helpers ---

    private static async Task<LiveServerSession> CreateSessionAsync(ServerSessionRegistry registry)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-theme-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var created = await registry.CreateAsync(new CreateServerSessionRequest(
            Cwd: root,
            NoTools: true,
            NoBuiltinTools: true,
            NoExtensions: true,
            NoSkills: true,
            NoPromptTemplates: true,
            NoThemes: true,
            NoContextFiles: true), CancellationToken.None);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        return live!;
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])), initial);
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}
