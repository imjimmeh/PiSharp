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
using PiSharp.Server.Contracts;
using PiSharp.Runtime.IO;
using PiSharp.Server.Runtime;
using Xunit;

namespace PiSharp.Advisor.Daemon.Tests;

/// <summary>
/// Verifies the advisor daemon-map: an <c>advisor_note</c> emitted by the advisor plugin over the
/// shared extension event bus is surfaced on the per-session daemon event lane
/// (<see cref="LiveServerSession"/>) as a flat <c>advisor_note</c> <see cref="AgentSessionEvent"/>
/// via <see cref="AgentSessionEvent.FromAdvisor"/>.
/// </summary>
public sealed class AdvisorDaemonMapTests
{
    [Fact]
    public async Task EmittedAdvisorNoteSurfacesOnDaemonEventLane()
    {
        var manager = new ExtensionManager();
        var runtime = await CreateRuntimeAsync(TempRoot(), manager);
        await using var live = new LiveServerSession("srv_test", runtime);

        await using var events = live.ReadEventsAsync().GetAsyncEnumerator();
        var nextTask = events.MoveNextAsync().AsTask();
        await Task.Yield();

        // The advisor plugin emits through the shared registry's event bus. Recreate that path:
        // LiveServerSession registered a "advisor_note" handler on runtime.ExtensionManager.Registry,
        // so emitting on a bus over the same registry reaches the forwarder.
        var bus = new ExtensionEventBus(manager.Registry, "advisor-under-test");
        var advisorEvent = new ExtensionAdvisorEvent(
            "sess_1",
            "assist_42",
            new ExtensionAdvisorNote("concern", "This approach may fail at runtime.", ToolName: "bash", Model: "test-model"));

        await bus.EmitAsync(ExtensionEventNames.AdvisorNote, advisorEvent);

        Assert.True(await nextTask);
        var envelope = events.Current;
        Assert.Equal("event", envelope.Type);
        Assert.Equal(ExtensionEventNames.AdvisorNote, envelope.Event.Type);

        var data = envelope.Event.Data;
        Assert.NotNull(data);
        Assert.Equal("sess_1", Get(data, "sessionId"));
        Assert.Equal("assist_42", Get(data, "turnId"));
        Assert.Equal("concern", Get(data, "kind"));
        Assert.Equal("This approach may fail at runtime.", Get(data, "text"));
        Assert.Equal("bash", Get(data, "toolName"));
        Assert.Equal("test-model", Get(data, "model"));
    }

    [Fact]
    public async Task AdvisorNoteIsSequencedAndRetainedAfterEmit()
    {
        var manager = new ExtensionManager();
        var runtime = await CreateRuntimeAsync(TempRoot(), manager);
        await using var live = new LiveServerSession("srv_test", runtime);

        var bus = new ExtensionEventBus(manager.Registry, "advisor-under-test");
        await bus.EmitAsync(ExtensionEventNames.AdvisorNote,
            new ExtensionAdvisorEvent("sess_1", "assist_1", new ExtensionAdvisorNote("note", "ok")));

        // Attach after emit: the event should be retained in the event log and replayed.
        var found = false;
        await foreach (var envelope in live.ReadEventsAsync())
        {
            if (envelope.Event.Type == ExtensionEventNames.AdvisorNote)
            {
                found = true;
                Assert.True(envelope.Sequence > 0);
                break;
            }
        }
        Assert.True(found);
    }

    [Fact]
    public async Task SessionWithoutExtensionManagerStillStreams()
    {
        var runtime = await CreateRuntimeAsync(TempRoot(), extensionManager: null);
        await using var live = new LiveServerSession("srv_test", runtime);

        // BindAdvisorEventForwarding must no-op cleanly when there is no extension manager.
        await using var events = live.ReadEventsAsync().GetAsyncEnumerator();
        var nextTask = events.MoveNextAsync().AsTask();
        await Task.Yield();
        await runtime.Harness.PromptAsync("hi");
        Assert.True(await nextTask);
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
