using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Harness.LoopEvents;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Agent.Tests.Harness;

public sealed class LoopEventStageTests
{
    [Fact]
    public async Task LoopEventPipelineExecutesStagesInOrder()
    {
        var executionOrder = new List<string>();
        var pipeline = new LoopEventPipeline([
            new DelegateStage(() => executionOrder.Add("one")),
            new DelegateStage(() => executionOrder.Add("two")),
            new DelegateStage(() => executionOrder.Add("three"))
        ]);

        await pipeline.ExecuteAsync(CreateContext(new AgentEvent.AgentStart()), CancellationToken.None);

        Assert.Equal(["one", "two", "three"], executionOrder);
    }

    [Fact]
    public async Task PersistenceStageQueuesMessageForMessageEnd()
    {
        var stage = new PersistenceStage();
        AgentMessage? queued = null;
        var message = AgentMessages.Assistant("done");

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.MessageEnd(message),
                queueWriteOrAppendAsync: queuedMessage =>
                {
                    queued = queuedMessage;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.Same(message, queued);
    }

    [Fact]
    public async Task PersistenceStageFlushesWritesForTurnEnd()
    {
        var stage = new PersistenceStage();
        CancellationToken observed = default;
        var flushCalls = 0;

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.TurnEnd(AgentMessages.Assistant("done"), []),
                flushWritesAsync: token =>
                {
                    flushCalls++;
                    observed = token;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.Equal(1, flushCalls);
        Assert.Equal(CancellationToken.None, observed);
    }

    [Fact]
    public async Task PersistenceStageFlushesWritesForAgentEnd()
    {
        var stage = new PersistenceStage();
        var flushCalls = 0;

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.AgentEnd([]),
                flushWritesAsync: _ =>
                {
                    flushCalls++;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.Equal(1, flushCalls);
    }

    [Fact]
    public async Task PhaseTransitionStageSetsPhaseToIdleForAgentEnd()
    {
        var stage = new PhaseTransitionStage();
        var observed = AgentHarnessPhase.Turn;

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.AgentEnd([]),
                setPhase: phase => observed = phase),
            CancellationToken.None);

        Assert.Equal(AgentHarnessPhase.Idle, observed);
    }

    [Fact]
    public async Task PhaseTransitionStageLeavesPhaseUnchangedForOtherEvents()
    {
        var stage = new PhaseTransitionStage();
        var observed = AgentHarnessPhase.Turn;

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.AgentStart(),
                setPhase: phase => observed = phase),
            CancellationToken.None);

        Assert.Equal(AgentHarnessPhase.Turn, observed);
    }

    [Fact]
    public async Task ExtensionDispatchStageCallsDispatcher()
    {
        var stage = new ExtensionDispatchStage();
        ExtensionEvent? observed = null;

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.AgentStart(),
                dispatchExtensionEventAsync: (evt, _) =>
                {
                    observed = evt;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(ExtensionEventNames.AgentStart, observed!.Name);
        Assert.IsType<AgentHarnessEvent.Core>(observed.OriginalEvent);
    }

    [Fact]
    public async Task ExtensionDispatchStageDispatchesOwnEventsThroughMappedExtensionEvent()
    {
        var stage = new ExtensionDispatchStage();
        ExtensionEvent? observed = null;
        var own = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("manual"));

        await stage.ExecuteAsync(
            CreateContext(
                own,
                HarnessEventKind.Own,
                dispatchExtensionEventAsync: (evt, _) =>
                {
                    observed = evt;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(ExtensionEventNames.SessionStart, observed!.Name);
        Assert.Same(own, observed.OriginalEvent);
    }

    [Fact]
    public async Task ExtensionDispatchStageSwallowsNonCancellationExceptions()
    {
        var stage = new ExtensionDispatchStage();

        var exception = await Record.ExceptionAsync(() =>
            stage.ExecuteAsync(
                CreateContext(
                    new AgentEvent.AgentStart(),
                    dispatchExtensionEventAsync: (_, _) => throw new InvalidOperationException("boom")),
                CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ExtensionDispatchStageRethrowsCancellationWhenRequested()
    {
        var stage = new ExtensionDispatchStage();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            stage.ExecuteAsync(
                CreateContext(
                    new AgentEvent.AgentStart(),
                    dispatchExtensionEventAsync: (_, _) => throw new OperationCanceledException(cts.Token)),
                cts.Token));
    }

    [Fact]
    public async Task ListenerNotificationStageInvokesEveryListener()
    {
        var stage = new ListenerNotificationStage();
        var calls = new List<string>();

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.AgentStart(),
                listeners:
                [
                    (_, _) =>
                    {
                        calls.Add("one");
                        return Task.CompletedTask;
                    },
                    (_, _) =>
                    {
                        calls.Add("two");
                        return Task.CompletedTask;
                    }
                ]),
            CancellationToken.None);

        Assert.Contains("one", calls);
        Assert.Contains("two", calls);
    }

    [Fact]
    public async Task ListenerNotificationStageStartsListenersConcurrently()
    {
        var stage = new ListenerNotificationStage();
        var firstListenerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondListenerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executeTask = stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.AgentStart(),
                listeners:
                [
                    async (_, _) =>
                    {
                        firstListenerStarted.SetResult();
                        await secondListenerStarted.Task;
                    },
                    (_, _) =>
                    {
                        secondListenerStarted.SetResult();
                        return Task.CompletedTask;
                    }
                ]),
            CancellationToken.None);

        await firstListenerStarted.Task;
        var completedTask = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(1)));
        secondListenerStarted.TrySetResult();

        Assert.Same(executeTask, completedTask);
    }

    [Fact]
    public async Task ListenerNotificationStageSwallowsListenerFailuresAndContinues()
    {
        var stage = new ListenerNotificationStage();
        var secondListenerRan = false;

        await stage.ExecuteAsync(
            CreateContext(
                new AgentEvent.AgentStart(),
                listeners:
                [
                    (_, _) => throw new InvalidOperationException("boom"),
                    (_, _) =>
                    {
                        secondListenerRan = true;
                        return Task.CompletedTask;
                    }
                ]),
            CancellationToken.None);

        Assert.True(secondListenerRan);
    }

    [Fact]
    public async Task ListenerNotificationStageRethrowsCancellationWhenRequested()
    {
        var stage = new ListenerNotificationStage();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            stage.ExecuteAsync(
                CreateContext(
                    new AgentEvent.AgentStart(),
                    listeners:
                    [
                        (_, _) => throw new OperationCanceledException(cts.Token)
                    ]),
                cts.Token));
    }

    [Fact]
    public void HarnessEventContextExposesConstructorValues()
    {
        var message = AgentMessages.Assistant("done");
        var harnessEvent = new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(message));
        var listeners = new List<Func<AgentHarnessEvent, CancellationToken, Task>>
        {
            (_, _) => Task.CompletedTask
        };
        var context = CreateContext(harnessEvent.Event, listeners: listeners, dispatchExtensionEventAsync: (_, _) => Task.CompletedTask);

        Assert.Same(harnessEvent.Event, context.CoreEvent);
        Assert.IsType<AgentHarnessEvent.Core>(context.Event);
        Assert.Equal(HarnessEventKind.CoreLoop, context.Kind);
        Assert.Single(context.Listeners);
        Assert.NotNull(context.DispatchExtensionEventAsync);
    }

    private static HarnessEventContext CreateContext(
        AgentEvent @event,
        Func<AgentMessage, Task>? queueWriteOrAppendAsync = null,
        Func<CancellationToken, Task>? flushWritesAsync = null,
        Action<AgentHarnessPhase>? setPhase = null,
        Func<ExtensionEvent, CancellationToken, Task>? dispatchExtensionEventAsync = null,
        IReadOnlyList<Func<AgentHarnessEvent, CancellationToken, Task>>? listeners = null)
        => CreateContext(
            new AgentHarnessEvent.Core(@event),
            HarnessEventKind.CoreLoop,
            queueWriteOrAppendAsync,
            flushWritesAsync,
            setPhase,
            dispatchExtensionEventAsync,
            listeners);

    private static HarnessEventContext CreateContext(
        AgentHarnessEvent @event,
        HarnessEventKind kind,
        Func<AgentMessage, Task>? queueWriteOrAppendAsync = null,
        Func<CancellationToken, Task>? flushWritesAsync = null,
        Action<AgentHarnessPhase>? setPhase = null,
        Func<ExtensionEvent, CancellationToken, Task>? dispatchExtensionEventAsync = null,
        IReadOnlyList<Func<AgentHarnessEvent, CancellationToken, Task>>? listeners = null)
        => new(
            @event,
            kind,
            queueWriteOrAppendAsync ?? (_ => Task.CompletedTask),
            flushWritesAsync ?? (_ => Task.CompletedTask),
            setPhase ?? (_ => { }),
            dispatchExtensionEventAsync,
            [],
            listeners ?? [],
            NullLogger.Instance,
            0,
            0);

    private sealed class DelegateStage(Action onExecute) : ILoopEventStage
    {
        public Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken)
        {
            onExecute();
            return Task.CompletedTask;
        }
    }
}
