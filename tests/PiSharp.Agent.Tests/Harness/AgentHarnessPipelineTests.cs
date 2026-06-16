using System.Reflection;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Agent.Tests.Harness;

public sealed class AgentHarnessPipelineTests
{
    [Fact]
    public void HarnessComposesLoopEventPipelineWithEpicStageOrder()
    {
        var session = CreateSession();
        var harness = Harness(session, FakeStream("ok"));

        var pipelineField = typeof(AgentHarness<JsonlSessionMetadata>).GetField("_loopEventPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pipelineField);

        var pipeline = pipelineField!.GetValue(harness);
        Assert.NotNull(pipeline);

        var stagesField = pipeline!.GetType().GetField("_stages", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stagesField);

        var stages = Assert.IsAssignableFrom<System.Collections.IEnumerable>(stagesField!.GetValue(pipeline));
        Assert.Equal(
            ["PersistenceStage", "PhaseTransitionStage", "ToolMiddlewareStage", "ExtensionDispatchStage", "ListenerNotificationStage"],
            stages.Cast<object>().Select(stage => stage.GetType().Name));
    }

    [Fact]
    public async Task AgentEndDispatchesExtensionsBeforeListenersAfterPersistenceAndPhaseTransition()
    {
        var session = CreateSession();
        var extensions = new ExtensionRegistry();
        var order = new List<string>();
        var extensionObservedPhase = AgentHarnessPhase.Turn;
        var listenerObservedPhase = AgentHarnessPhase.Turn;
        var extensionObservedMessages = 0;
        var listenerObservedMessages = 0;

        var harness = Harness(session, FakeStream("ok"), extensions);
        extensions.RegisterHandler("extension:test", ExtensionEventNames.AgentEnd, async (_, _) =>
        {
            order.Add("extension");
            extensionObservedPhase = harness.Phase;
            extensionObservedMessages = (await session.GetEntriesAsync()).OfType<MessageEntry>().Count();
        });

        using var _ = harness.Subscribe(async (evt, _) =>
        {
            if (evt is not AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd })
            {
                return;
            }

            order.Add("listener");
            listenerObservedPhase = harness.Phase;
            listenerObservedMessages = (await session.GetEntriesAsync()).OfType<MessageEntry>().Count();
        });

        await harness.PromptAsync("hello");

        Assert.Equal(["extension", "listener"], order);
        Assert.Equal(AgentHarnessPhase.Idle, extensionObservedPhase);
        Assert.Equal(AgentHarnessPhase.Idle, listenerObservedPhase);
        Assert.Equal(2, extensionObservedMessages);
        Assert.Equal(2, listenerObservedMessages);
    }

    [Fact]
    public async Task BeforeAgentStartDispatchesExtensionsBeforeListenersThroughPipeline()
    {
        var session = CreateSession();
        var extensions = new ExtensionRegistry();
        var order = new List<string>();
        var harness = Harness(session, FakeStream("ok"), extensions);

        extensions.RegisterHandler("extension:test", ExtensionEventNames.BeforeAgentStart, (evt, _) =>
        {
            order.Add("extension");
            evt.ModifyBeforeAgentStart("mutated prompt");
            return Task.CompletedTask;
        });
        using var _ = harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforeAgentStart })
            {
                order.Add("listener");
            }

            return Task.CompletedTask;
        });

        await harness.PromptAsync("hello");

        Assert.Equal(["extension", "listener"], order);
    }

    [Fact]
    public async Task PromptContinuesWhenExtensionHandlerThrows()
    {
        var session = CreateSession();
        var extensions = new ExtensionRegistry();
        var listenerSawAgentEnd = false;
        var harness = Harness(session, FakeStream("ok"), extensions);

        extensions.RegisterHandler("extension:test", ExtensionEventNames.AgentEnd, (_, _) => throw new InvalidOperationException("boom"));
        using var _ = harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd })
            {
                listenerSawAgentEnd = true;
            }

            return Task.CompletedTask;
        });

        var result = await harness.PromptAsync("hello");

        Assert.Equal("ok", result.Content.OfType<TextContent>().Single().Text);
        Assert.True(listenerSawAgentEnd);
    }

    [Fact]
    public async Task PromptStartsExtensionHandlersForSameEventConcurrently()
    {
        var session = CreateSession();
        var extensions = new ExtensionRegistry();
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = Harness(session, FakeStream("ok"), extensions);

        extensions.RegisterHandler("extension:first", ExtensionEventNames.AgentEnd, async (_, _) =>
        {
            firstHandlerStarted.SetResult();
            await secondHandlerStarted.Task;
        });
        extensions.RegisterHandler("extension:second", ExtensionEventNames.AgentEnd, (_, _) =>
        {
            secondHandlerStarted.SetResult();
            return Task.CompletedTask;
        });

        var promptTask = harness.PromptAsync("hello");

        await firstHandlerStarted.Task;
        var completedTask = await Task.WhenAny(promptTask, Task.Delay(TimeSpan.FromSeconds(1)));
        secondHandlerStarted.TrySetResult();

        Assert.Same(promptTask, completedTask);
        Assert.Equal("ok", (await promptTask).Content.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public async Task PromptContinuesWhenListenerThrowsAndSubsequentListenerStillRuns()
    {
        var session = CreateSession();
        var harness = Harness(session, FakeStream("ok"));
        var secondListenerSawAgentEnd = false;

        using var _ = harness.Subscribe((evt, _) =>
            evt is AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd }
                ? throw new InvalidOperationException("boom")
                : Task.CompletedTask);
        using var __ = harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd })
            {
                secondListenerSawAgentEnd = true;
            }

            return Task.CompletedTask;
        });

        var result = await harness.PromptAsync("hello");

        Assert.Equal("ok", result.Content.OfType<TextContent>().Single().Text);
        Assert.True(secondListenerSawAgentEnd);
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(Session<JsonlSessionMetadata> session, AgentStreamAsync stream, ExtensionRegistry? extensions = null)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, [], Extensions: extensions));

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "test.jsonl")));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("done"));

    private static AgentStreamAsync FakeStream(string text)
    {
        AgentStreamAsync stream = (_, _, _, _) => StreamHelper(text);
        return stream;
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamHelper(string text)
    {
        var message = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(message);
    }
}
