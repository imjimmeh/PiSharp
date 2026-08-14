using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.PlanMode;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.PlanMode.Rpc.Tests;

/// <summary>
/// End-to-end daemon surface for plan mode: <c>set_plan_mode</c>/<c>get_plan_mode</c> drive the real
/// <see cref="PlanModeExtension"/> machine (tool restriction + captures), each transition streams a
/// <c>plan_mode_changed</c> envelope onto the session's retained event log (the daemon wire), and
/// replay exposes the transition history.
/// </summary>
public sealed class PlanModeDaemonIntegrationTests
{
    private IReadOnlyList<string>? _restrictedTools;

    [Fact]
    public async Task SetPlanMode_PlanningThenExecuting_DrivesMachineAndStreamsEvents()
    {
        var (handler, live, manager, _) = await CreateIntegrationSessionAsync();

        // Enter planning.
        var planning = await DispatchSetPlanMode(handler, live.Id, "planning");
        Assert.True(planning.Success, planning.Error?.Message);
        var planningState = Assert.IsType<ServerPlanModeState>(planning.Data);
        Assert.Equal("planning", planningState.Phase);
        Assert.NotNull(planningState.PlanFile);
        Assert.Equal(["read", "grep", "find", "ls"], planningState.RestrictedToolNames);
        Assert.Equal(["read", "grep", "find", "ls"], _restrictedTools);

        // The transition is on the wire (retained event log).
        Assert.Contains(Replay(live), e => e.Event.Type == PlanModeService.PlanModeChangedEvent && PayloadPhase(e) == "planning");

        // A planning turn produced a plan body via agent_end capture (persisted as draft).
        await DispatchAgentEndCaptureAsync(manager, "The plan is to build a feature.");
        var planFile = planningState.PlanFile;
        Assert.Contains("status: draft", File.ReadAllText(planFile!));

        // Approve.
        var executing = await DispatchSetPlanMode(handler, live.Id, "executing");
        Assert.True(executing.Success, executing.Error?.Message);
        var executingState = Assert.IsType<ServerPlanModeState>(executing.Data);
        Assert.Equal("executing", executingState.Phase);
        Assert.Contains("status: approved", File.ReadAllText(planFile!));

        var rejected = await DispatchSetPlanMode(handler, live.Id, "executing");
        Assert.False(rejected.Success);
        Assert.Equal("command_failed", rejected.Error?.Code);

        Assert.Contains(Replay(live), e => e.Event.Type == PlanModeService.PlanModeChangedEvent && PayloadPhase(e) == "executing");
    }

    [Fact]
    public async Task SetPlanMode_ApproveWithoutBody_ReturnsCommandFailed()
    {
        var (handler, live, _, _) = await CreateIntegrationSessionAsync();

        var planning = await DispatchSetPlanMode(handler, live.Id, "planning");
        Assert.True(planning.Success);

        // No agent_end captured yet → approval must fail.
        var approve = await DispatchSetPlanMode(handler, live.Id, "executing");
        Assert.False(approve.Success);
        Assert.Equal("command_failed", approve.Error?.Code);
        Assert.Contains("no plan body", approve.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPlanMode_ReturnsAttachSnapshotBeforeAnyTransition()
    {
        var (handler, live, _, _) = await CreateIntegrationSessionAsync();
        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.GetPlanMode,
            serverSessionId = live.Id
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var state = Assert.IsType<ServerPlanModeState>(response.Data);
        Assert.Equal("inactive", state.Phase);
    }

    [Fact]
    public async Task SetPlanMode_InvalidPhase_ReturnsCommandFailed()
    {
        var (handler, live, _, _) = await CreateIntegrationSessionAsync();
        var response = await DispatchSetPlanMode(handler, live.Id, "frobnicate");
        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task SetPlanMode_UnknownSession_Fails()
    {
        var (handler, _, _, _) = await CreateIntegrationSessionAsync();
        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "1",
            type = ServerCommandTypes.SetPlanMode,
            serverSessionId = "missing",
            phase = "planning"
        }, ServerJsonSerializer.Options));
        Assert.False(response.Success);
    }

    // --- helpers ---

    private static Task<ServerResponse> DispatchSetPlanMode(PiServerWebSocketHandler handler, string sessionId, string phase)
        => handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"),
            type = ServerCommandTypes.SetPlanMode,
            serverSessionId = sessionId,
            phase
        }, ServerJsonSerializer.Options));

    private static IReadOnlyList<ServerEventEnvelope> Replay(LiveServerSession live)
        => live.EventLog.ReplayFrom(1).Events;

    private static string? PayloadPhase(ServerEventEnvelope envelope)
    {
        if (envelope.Event.Data is null) return null;
        var json = JsonSerializer.Serialize(envelope.Event.Data, ServerJsonSerializer.Options);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("phase", out var phase) ? phase.GetString() : null;
    }

    private static async Task DispatchAgentEndCaptureAsync(ExtensionManager manager, string planBody)
    {
        var agentEnd = new AgentEvent.AgentEnd(new AgentMessage[] { AgentMessages.Assistant(planBody) });
        var evt = new ExtensionEvent(ExtensionEventNames.AgentEnd, new AgentHarnessEvent.Core(agentEnd), agentEnd);
        foreach (var registration in manager.Registry.HandlersFor(ExtensionEventNames.AgentEnd))
        {
            await registration.Value.Handler(evt, CancellationToken.None);
        }
    }

    private async Task<(PiServerWebSocketHandler Handler, LiveServerSession Live, ExtensionManager Manager, string Root)> CreateIntegrationSessionAsync()
    {
        var root = TempRoot();
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        var manager = new ExtensionManager();
        await manager.InitializeAsync(
            new ExtensionDescriptor("plan-mode", "PiSharp Plan Mode", "0.1.0", SourceId: "pi:extension:plan-mode"),
            new PlanModeExtension(),
            binding);

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        PiSharp.Runtime.SessionRuntime runtime = null!;
        runtime = new PiSharp.Runtime.SessionRuntime(repo, createOptions, BuildHarness, initial, extensionManager: manager);
        binding.GetAllToolsAsync = _ => Task.FromResult<IReadOnlyList<string>>(["read", "grep", "find", "ls", "edit"]);
        binding.SetActiveToolsAsync = (names, _) => { _restrictedTools = names; return Task.CompletedTask; };
        binding.EmitClientEventAsync = (name, payload, ct) => runtime.Harness.PublishOwnEventAsync(new AgentHarnessOwnEvent.CustomEvent(name, payload), ct);

        var registry = new ServerSessionRegistry((_, ct) => Task.FromResult(runtime), TimeSpan.FromHours(1));
        var handler = new PiServerWebSocketHandler(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance);
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd = root }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        return (handler, live!, manager, root);
    }

    private static AgentHarness<JsonlSessionMetadata> BuildHarness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-planmode-rpc-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
