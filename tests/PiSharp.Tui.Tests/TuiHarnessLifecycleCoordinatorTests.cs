using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Harness;
using PiSharp.Tui.Interactive.Shell;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiHarnessLifecycleCoordinatorTests
{
    private static TuiRenderState EmptyState() => TuiRenderState.Empty(
        "sid", "file", new ModelDescriptor("test", "m", "test"), ThinkingLevel.Off, null);

    private static TuiFooterSnapshot EmptyFooterSnapshot() => null!;

    [Fact]
    public async Task RefreshIsNoOpWhenHarnessUnchanged()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = EmptyState();
        var store = new RenderStateStore(state);
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        var runtime = TuiIntegrationTestHost.CreateRuntimeFacade();
        var sessionContext = new TuiSessionContext { CurrentRuntime = runtime };
        var options = new TuiHostOptions(runtime, "sid", null, _ => Task.FromResult<string?>(null));
        var coordinator = new TuiHarnessLifecycleCoordinator(
            sessionContext, options, appContext, renderCoordinator,
            store,
            _ => Task.FromResult<TuiSessionSnapshot?>(null), (_, _) => { }, null);
        coordinator.BindInitialHarnessSubscription();

        await coordinator.RefreshAfterPossibleSessionChangeAsync(CancellationToken.None);
        appContext.Dispatcher.PumpPosted();

        Assert.Same(runtime, sessionContext.CurrentRuntime); // unchanged
    }
    private sealed class CapturingFacade : ITuiRuntimeFacade
    {
        public Func<AgentHarnessEvent, CancellationToken, Task>? Listener { get; private set; }
        public AgentHarnessPhase Phase => AgentHarnessPhase.Idle;
        public ModelDescriptor Model => new("p", "m", "name");
        public ThinkingLevel ThinkingLevel => ThinkingLevel.Off;
        public IReadOnlyList<string> ActiveToolNames => [];
        public Func<CancellationToken, Task>? OnHarnessReplaced { get; set; }
        public IDisposable Subscribe(Func<AgentHarnessEvent, CancellationToken, Task> listener)
        {
            Listener = listener;
            return new DummyDisposable();
        }
        public void Abort() { }
        public Task PromptAsync(string text, IReadOnlyList<ImageContent> images, CancellationToken token) => Task.CompletedTask;
        public void Steer(AgentMessage message) { }
        private sealed class DummyDisposable : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Event_reduction_lands_on_store_and_schedules_render()
    {
        var facade = new CapturingFacade();
        var store = new RenderStateStore(
            TuiRenderState.Empty("s1", null, new ModelDescriptor("p", "m", "n"), ThinkingLevel.Off, null));
        var renderCalled = new ManualResetEventSlim(false);
        var subscription = new TuiHarnessSubscription(
            getCurrentRuntime: () => facade,
            store,
            scheduleRender: _ => renderCalled.Set(),
            dispatch: action => action(),
            resolveTool: null,
            loadSessionSnapshot: _ => Task.FromResult<TuiSessionSnapshot?>(null),
            applySessionSnapshot: (_, _) => { });
        subscription.Bind();

        var listener = facade.Listener
            ?? throw new InvalidOperationException("subscription did not subscribe to the facade");

        // Emit a TurnStart from a background thread — the daemon/background side of the connection.
        await Task.Run(() => listener(new AgentHarnessEvent.Core(new AgentEvent.TurnStart()), CancellationToken.None));

        Assert.True(renderCalled.Wait(TimeSpan.FromSeconds(2))); // render requested after the batch
        Assert.True(store.Snapshot().IsBusy);                     // TurnStart -> IsBusy=true
        subscription.Dispose();
    }
}
