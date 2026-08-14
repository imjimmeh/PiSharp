using System.Runtime.CompilerServices;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Runtime;
using Xunit;

namespace PiSharp.Advisor.Daemon.Tests;

/// <summary>
/// Covers the reserved-name custom-event lane: emitting <c>advisor_note</c> through the harness
/// custom-event path (<c>AgentHarness.PublishOwnEventAsync(CustomEvent)</c> — the delegate behind
/// <c>IExtensionApi.EmitClientEventAsync</c>) must map onto the dedicated
/// <see cref="AgentHarnessOwnEvent.AdvisorNote"/> variant instead of being rejected by
/// <see cref="AgentSessionEvent.ValidateCustomEventName"/> (which reserves the name for the flat
/// <c>advisor_note</c> session event). Arbitrary custom names keep their existing validation.
/// </summary>
public sealed class AdvisorNoteCustomEventLaneTests
{
    [Fact]
    public async Task PublishAdvisorNoteCustomEvent_MapsToFlatEventOnDaemonLane()
    {
        var manager = new ExtensionManager();
        var runtime = await CreateRuntimeAsync(TempRoot(), manager);
        await using var live = new LiveServerSession("srv_test", runtime);

        var advisorEvent = new ExtensionAdvisorEvent(
            "sess_1",
            "assist_42",
            new ExtensionAdvisorNote("blocker", "The plan is incorrect.", ToolName: "bash", Model: "test-model"));

        // This used to throw ArgumentException: the runtime binding routes EmitClientEventAsync
        // into PublishOwnEventAsync(CustomEvent), and "advisor_note" is a reserved event name.
        await runtime.Harness.PublishOwnEventAsync(
            new AgentHarnessOwnEvent.CustomEvent(ExtensionEventNames.AdvisorNote, advisorEvent),
            CancellationToken.None);

        // Exactly one flat advisor_note on the daemon lane: the harness listener maps the
        // AdvisorNote variant via FromAdvisor; the daemon:advisor registry handler must not
        // re-fire for the harness-dispatched (non-ExtensionAdvisorEvent-payload) event.
        var envelope = Assert.Single(
            live.EventLog.ReplayFrom(0).Events.Where(e => e.Event.Type == ExtensionEventNames.AdvisorNote));
        Assert.Equal("event", envelope.Type);
        var data = envelope.Event.Data;
        Assert.NotNull(data);
        Assert.Equal("sess_1", Get(data, "sessionId"));
        Assert.Equal("assist_42", Get(data, "turnId"));
        Assert.Equal("blocker", Get(data, "kind"));
        Assert.Equal("The plan is incorrect.", Get(data, "text"));
        Assert.Equal("bash", Get(data, "toolName"));
        Assert.Equal("test-model", Get(data, "model"));
    }

    [Fact]
    public async Task PublishReservedNameWithoutDedicatedMapping_StillThrows()
    {
        var runtime = await CreateRuntimeAsync(TempRoot(), new ExtensionManager());
        await using var live = new LiveServerSession("srv_test", runtime);

        // Reserved names with no dedicated flat mapping (core session events) stay rejected.
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Harness.PublishOwnEventAsync(
            new AgentHarnessOwnEvent.CustomEvent("session_start", new { reason = "forged" }),
            CancellationToken.None));
    }

    [Fact]
    public async Task PublishArbitraryCustomEvent_StillValidatedAndForwarded()
    {
        var runtime = await CreateRuntimeAsync(TempRoot(), new ExtensionManager());
        await using var live = new LiveServerSession("srv_test", runtime);

        await runtime.Harness.PublishOwnEventAsync(
            new AgentHarnessOwnEvent.CustomEvent("my_custom_event", new { ok = true }),
            CancellationToken.None);

        var envelope = Assert.Single(
            live.EventLog.ReplayFrom(0).Events.Where(e => e.Event.Type == "my_custom_event"));
        Assert.NotNull(envelope.Event.Data);
        Assert.True((bool)Get(envelope.Event.Data, "ok")!);
    }

    [Fact]
    public async Task PublishAdvisorNoteCustomEvent_WrongPayloadType_Throws()
    {
        var runtime = await CreateRuntimeAsync(TempRoot(), new ExtensionManager());
        await using var live = new LiveServerSession("srv_test", runtime);

        // The reserved mapping needs the dedicated payload type; anything else is rejected.
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => runtime.Harness.PublishOwnEventAsync(
            new AgentHarnessOwnEvent.CustomEvent(ExtensionEventNames.AdvisorNote, "not-an-advisor-event"),
            CancellationToken.None));
        Assert.Contains(nameof(ExtensionAdvisorEvent), exception.Message);
    }

    [Fact]
    public void FromOwn_AdvisorNoteVariant_FlattensViaFromAdvisor()
    {
        var advisorEvent = new ExtensionAdvisorEvent(
            "sess_9",
            "assist_7",
            new ExtensionAdvisorNote("concern", "Check the retry loop.", ToolName: "tool_x", Model: "m1"));

        var flat = AgentSessionEvent.FromOwn(new AgentHarnessOwnEvent.AdvisorNote(advisorEvent));

        Assert.Equal(ExtensionEventNames.AdvisorNote, flat.Type);
        Assert.NotNull(flat.Data);
        Assert.Equal("sess_9", Get(flat.Data, "sessionId"));
        Assert.Equal("assist_7", Get(flat.Data, "turnId"));
        Assert.Equal("concern", Get(flat.Data, "kind"));
        Assert.Equal("Check the retry loop.", Get(flat.Data, "text"));
        Assert.Equal("tool_x", Get(flat.Data, "toolName"));
        Assert.Equal("m1", Get(flat.Data, "model"));
    }

    [Fact]
    public void MapReservedCustomEvent_NonReservedName_ReturnsNull()
    {
        var result = AgentSessionEvent.MapReservedCustomEvent(
            new AgentHarnessOwnEvent.CustomEvent("my_custom_event", new { ok = true }));

        Assert.Null(result);
    }

    [Fact]
    public void MapReservedCustomEvent_AdvisorNote_ReturnsAdvisorNoteVariant()
    {
        var advisorEvent = new ExtensionAdvisorEvent(
            "sess_1", "assist_1", new ExtensionAdvisorNote("note", "ok"));

        var result = AgentSessionEvent.MapReservedCustomEvent(
            new AgentHarnessOwnEvent.CustomEvent(ExtensionEventNames.AdvisorNote, advisorEvent));

        var advisor = Assert.IsType<AgentHarnessOwnEvent.AdvisorNote>(result);
        Assert.Same(advisorEvent, advisor.Event);
    }

    [Fact]
    public void MapReservedCustomEvent_AdvisorNoteWrongPayload_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => AgentSessionEvent.MapReservedCustomEvent(
            new AgentHarnessOwnEvent.CustomEvent(ExtensionEventNames.AdvisorNote, "not-an-advisor-event")));
        Assert.Contains(nameof(ExtensionAdvisorEvent), exception.Message);
    }

    private static object? Get(object? data, string property)
        => data?.GetType().GetProperty(property)?.GetValue(data);

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root, ExtensionManager? extensionManager)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Extensions: extensionManager?.Registry)), initial, extensionManager: extensionManager);
    }

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
        var root = Path.Combine(Path.GetTempPath(), "pisharp-advisor-daemon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
