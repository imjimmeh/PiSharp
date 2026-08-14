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
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Server.Tests;

/// <summary>
/// Daemon theme command surface (plan C8): <c>get_theme</c>/<c>list_themes</c>/<c>set_theme</c>
/// dispatch against a shared <see cref="ThemeRegistry"/>, <c>theme_changed</c> broadcast on live
/// session streams, and session-creation registry feeding. The fake runtime has no theme resources,
/// so registry documents are fed directly; the command behavior is what is exercised here.
/// </summary>
public sealed class ThemeCommandTests
{
    [Fact]
    public async Task GetTheme_WithoutActiveTheme_ReturnsNotAvailable()
    {
        var (handler, _) = CreateHandler(out var themes);
        var created = await CreateSessionAsync(handler);
        var command = JsonSerializer.Serialize(new
        {
            id = "g", type = ServerCommandTypes.GetTheme, serverSessionId = created.ServerSessionId,
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("not_available", response.Error?.Code);
    }

    [Fact]
    public async Task GetTheme_ReturnsActiveDocumentFromRegistry()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark", "tokens": { "background": "#1e1e1e" } }"""));
        var (handler, themes) = CreateHandler(out _);
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        Assert.True(themes.TrySetActive("Dark"));
        var created = await CreateSessionAsync(handler);
        var command = JsonSerializer.Serialize(new
        {
            id = "g", type = ServerCommandTypes.GetTheme, serverSessionId = created.ServerSessionId,
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var payload = JsonSerializer.Serialize(response.Data, ServerJsonSerializer.Options);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("Dark", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("#1e1e1e", json.RootElement.GetProperty("tokens").GetProperty("background").GetString());
    }

    [Fact]
    public async Task ListThemes_ReturnsNamesOnly()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(
            ("Dark", """{ "name": "Dark" }"""),
            ("Light", """{ "name": "Light" }"""));
        var (handler, themes) = CreateHandler(out _);
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var created = await CreateSessionAsync(handler);
        var command = JsonSerializer.Serialize(new
        {
            id = "l", type = ServerCommandTypes.ListThemes, serverSessionId = created.ServerSessionId,
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var payload = JsonSerializer.Serialize(response.Data, ServerJsonSerializer.Options);
        using var json = JsonDocument.Parse(payload);
        var names = json.RootElement.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "Dark", "Light" }, names);
    }

    [Fact]
    public async Task ListThemes_WithoutServerSession_IsDaemonGlobal()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var (handler, themes) = CreateHandler(out _);
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var command = JsonSerializer.Serialize(new { id = "l", type = ServerCommandTypes.ListThemes }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var payload = JsonSerializer.Serialize(response.Data, ServerJsonSerializer.Options);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("Dark", Assert.Single(json.RootElement.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task SetTheme_UnknownName_ReturnsThemeNotFound()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var (handler, themes) = CreateHandler(out _);
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var created = await CreateSessionAsync(handler);
        var command = JsonSerializer.Serialize(new
        {
            id = "s", type = ServerCommandTypes.SetTheme, serverSessionId = created.ServerSessionId, name = "Missing",
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("theme_not_found", response.Error?.Code);
        Assert.Null(themes.ActiveName);
    }

    [Fact]
    public async Task SetTheme_ActivatesAndBroadcastsThemeChangedOnLiveSessions()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark", "tokens": { "background": "#1e1e1e" } }"""));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var (handler, themes) = CreateHandler(out _, registry);
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var created = await CreateSessionAsync(handler, registry);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        var command = JsonSerializer.Serialize(new
        {
            id = "s", type = ServerCommandTypes.SetTheme, serverSessionId = created.ServerSessionId, name = "Dark",
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        Assert.Equal("Dark", themes.ActiveName);
        Assert.NotNull(themes.ActiveDocument);

        var changed = live!.EventLog.ReplayFrom(0).Events.Single(envelope => envelope.Event.Type == "theme_changed");
        var data = ThemeRegistryTests.SerializeData(changed.Event.Data);
        Assert.Equal("Dark", data.GetProperty("name").GetString());
        Assert.Equal("#1e1e1e", data.GetProperty("document").GetProperty("tokens").GetProperty("background").GetString());
    }

    [Fact]
    public async Task SetTheme_WithoutServerSession_BroadcastsToEveryLiveSession()
    {
        using var dir = ThemeRegistryTests.NewThemeDir(("Dark", """{ "name": "Dark" }"""));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var (handler, themes) = CreateHandler(out _, registry);
        await themes.MergeAsync([dir.Path], CancellationToken.None);
        var first = await CreateSessionAsync(handler, registry);
        var second = await CreateSessionAsync(handler, registry);
        Assert.True(registry.TryGet(first.ServerSessionId, out var liveA));
        Assert.True(registry.TryGet(second.ServerSessionId, out var liveB));
        var command = JsonSerializer.Serialize(new { id = "s", type = ServerCommandTypes.SetTheme, name = "Dark" }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        Assert.Single(liveA!.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "theme_changed");
        Assert.Single(liveB!.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "theme_changed");
    }

    [Fact]
    public async Task SetTheme_UnknownServerSession_Fails()
    {
        var (handler, _) = CreateHandler(out _);
        var command = JsonSerializer.Serialize(new
        {
            id = "s", type = ServerCommandTypes.SetTheme, serverSessionId = "missing", name = "Dark",
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task CreateSession_WithoutThemePaths_LeavesRegistryEmpty()
    {
        var (handler, themes) = CreateHandler(out _);
        var created = await CreateSessionAsync(handler);

        Assert.NotNull(created);
        Assert.Empty(themes.Documents);
    }

    // --- shared helpers ---

    private static (PiServerWebSocketHandler Handler, ThemeRegistry Themes) CreateHandler(out ThemeRegistry themes, ServerSessionRegistry? registry = null)
    {
        themes = new ThemeRegistry();
        var handler = new PiServerWebSocketHandler(
            registry ?? new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)),
            new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }),
            NullLogger<PiServerWebSocketHandler>.Instance,
            uiBridge: null,
            delegates: null,
            themeRegistry: themes);
        return (handler, themes);
    }

    private static async Task<ServerSessionCreated> CreateSessionAsync(PiServerWebSocketHandler handler, ServerSessionRegistry? registry = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-theme-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "create", type = ServerCommandTypes.CreateSession, cwd = root,
            noTools = true, noBuiltinTools = true, noExtensions = true, noSkills = true,
            noPromptTemplates = true, noThemes = true, noContextFiles = true,
        }, ServerJsonSerializer.Options));
        Assert.True(response.Success, response.Error?.Message);
        return Assert.IsType<ServerSessionCreated>(response.Data);
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
