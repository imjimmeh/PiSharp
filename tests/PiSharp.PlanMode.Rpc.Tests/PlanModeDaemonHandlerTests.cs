using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.PlanMode.Rpc.Tests;

/// <summary>
/// Daemon <c>set_plan_mode</c>/<c>get_plan_mode</c> handler mechanics against an in-memory fake
/// <see cref="IPlanModeDaemonSurface"/>, so the wire behavior is covered without coupling to the
/// real plan-mode machine (which the integration tests exercise).
/// </summary>
public sealed class PlanModeDaemonHandlerTests
{
    [Fact]
    public async Task SetPlanMode_Planning_AppliesPhaseAndReturnsState()
    {
        var surface = new FakePlanModeSurface
        {
            OnApply = phase => Task.FromResult(new ExtensionPlanModeState("planning", ["read", "grep", "find", "ls"], null, "/tmp/plan.md"))
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.SetPlanMode,
            serverSessionId = ctx.SessionId,
            phase = "planning"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.Equal("planning", surface.LastPhase);
        Assert.Equal(1, surface.ApplyCalls);
        var state = Assert.IsType<ServerPlanModeState>(response.Data);
        Assert.Equal("planning", state.Phase);
        Assert.Equal(["read", "grep", "find", "ls"], state.RestrictedToolNames);
    }

    [Fact]
    public async Task SetPlanMode_Executing_ApprovesAndReturnsState()
    {
        var surface = new FakePlanModeSurface
        {
            OnApply = phase => Task.FromResult(new ExtensionPlanModeState("executing", [], null, "/tmp/plan.md"))
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.SetPlanMode,
            serverSessionId = ctx.SessionId,
            phase = "executing"
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.Equal("executing", surface.LastPhase);
        var state = Assert.IsType<ServerPlanModeState>(response.Data);
        Assert.Equal("executing", state.Phase);
    }

    [Fact]
    public async Task SetPlanMode_InvalidPhase_ReturnsCommandFailed()
    {
        var surface = new FakePlanModeSurface
        {
            OnApply = phase => throw new ArgumentException($"Unknown plan-mode phase '{phase}'.")
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.SetPlanMode,
            serverSessionId = ctx.SessionId,
            phase = "bogus"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
        Assert.Contains("Unknown plan-mode phase", response.Error?.Message);
    }

    [Fact]
    public async Task SetPlanMode_IllegalTransition_ReturnsCommandFailed()
    {
        var surface = new FakePlanModeSurface
        {
            OnApply = phase => throw new InvalidOperationException("Cannot approve while phase is 'inactive'.")
        };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.SetPlanMode,
            serverSessionId = ctx.SessionId,
            phase = "executing"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
        Assert.Contains("Cannot approve", response.Error?.Message);
    }

    [Fact]
    public async Task SetPlanMode_WithoutExtensionLoaded_ReturnsPlanModeUnavailable()
    {
        // Runtime has no extension manager / no plan-mode extension loaded.
        var registry = new ServerSessionRegistry(async (request, _) => new SessionRuntimeResult(await CreatePlainRuntimeAsync(request.Cwd), null), TimeSpan.FromHours(1));
        var handler = new PiServerWebSocketHandler(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance);
        var created = await CreateSessionAsync(handler);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.SetPlanMode,
            serverSessionId = created.ServerSessionId,
            phase = "planning"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("plan_mode_unavailable", response.Error?.Code);
    }

    [Fact]
    public async Task GetPlanMode_ReturnsCurrentSnapshot()
    {
        var surface = new FakePlanModeSurface { Current = new ExtensionPlanModeState("planning", ["read"], "claude/sonnet", "/tmp/plan.md") };
        var ctx = await CreateHandlerWithSurfaceAsync(surface);

        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.GetPlanMode,
            serverSessionId = ctx.SessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var state = Assert.IsType<ServerPlanModeState>(response.Data);
        Assert.Equal("planning", state.Phase);
        Assert.Equal("claude/sonnet", state.PlanningModel);
        Assert.Equal("/tmp/plan.md", state.PlanFile);
        Assert.Equal(0, surface.ApplyCalls);
    }

    [Fact]
    public async Task GetPlanMode_WrongSession_Fails()
    {
        var ctx = await CreateHandlerWithSurfaceAsync(new FakePlanModeSurface());
        var response = await ctx.Handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.GetPlanMode,
            serverSessionId = "missing"
        }, ServerJsonSerializer.Options));
        Assert.False(response.Success);
    }

    // --- helpers ---

    private static async Task<(PiServerWebSocketHandler Handler, string SessionId)> CreateHandlerWithSurfaceAsync(FakePlanModeSurface surface)
    {
        var manager = new PiSharp.Extensions.ExtensionManager();
        await manager.InitializeAsync(
            new PiSharp.Extensions.ExtensionDescriptor("plan-mode", "PiSharp Plan Mode", "0.1.0", SourceId: "pi:extension:plan-mode"),
            surface,
            new PiSharp.Extensions.ExtensionRuntimeBinding(TempRoot(), false, PiSharp.Extensions.NoExtensionUi.Instance));
        var registry = new ServerSessionRegistry(async (request, ct) => new SessionRuntimeResult(await CreateRuntimeAsync(request.Cwd, manager), null), TimeSpan.FromHours(1));
        var handler = new PiServerWebSocketHandler(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance);
        var created = await CreateSessionAsync(handler);
        return (handler, created.ServerSessionId);
    }

    private static async Task<ServerSessionCreated> CreateSessionAsync(PiServerWebSocketHandler handler)
    {
        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options));
        return Assert.IsType<ServerSessionCreated>(response.Data);
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root, PiSharp.Extensions.ExtensionManager manager)
        => await BuildRuntimeAsync(root, manager);

    private static async Task<PiSharp.Runtime.SessionRuntime> CreatePlainRuntimeAsync(string root)
        => await BuildRuntimeAsync(root, manager: null);

    private static async Task<PiSharp.Runtime.SessionRuntime> BuildRuntimeAsync(string root, PiSharp.Extensions.ExtensionManager? manager)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, BuildHarness, initial, extensionManager: manager);
    }

    private static AgentHarness<JsonlSessionMetadata> BuildHarness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(PiSharp.Abstractions.Messages.AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = PiSharp.Abstractions.Messages.AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-planmode-rpc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>In-memory <see cref="IExtension"/> + <see cref="IPlanModeDaemonSurface"/> used to exercise handler dispatch.</summary>
    private sealed class FakePlanModeSurface : PiSharp.Extensions.IExtension, PiSharp.Extensions.IPlanModeDaemonSurface
    {
        public int ApplyCalls { get; private set; }
        public string? LastPhase { get; private set; }
        public Func<string, Task<PiSharp.Extensions.ExtensionPlanModeState>>? OnApply { get; set; }
        public PiSharp.Extensions.ExtensionPlanModeState Current { get; set; } = new("inactive", [], null, null);

        public Task InitializeAsync(PiSharp.Extensions.IExtensionApi api, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<PiSharp.Extensions.ExtensionPlanModeState> ApplyPhaseAsync(string phase, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            LastPhase = phase;
            return await OnApply!(phase);
        }
    }
}
