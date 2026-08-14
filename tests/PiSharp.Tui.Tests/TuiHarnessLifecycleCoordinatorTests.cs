using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Models;
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
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        var runtime = TuiIntegrationTestHost.CreateRuntimeFacade();
        var sessionContext = new TuiSessionContext { CurrentRuntime = runtime };
        var options = new TuiHostOptions(runtime, "sid", null, _ => Task.FromResult<string?>(null));
        var coordinator = new TuiHarnessLifecycleCoordinator(
            sessionContext, options, appContext, renderCoordinator,
            () => state, s => state = s,
            _ => Task.FromResult<TuiSessionSnapshot?>(null), (_, _) => { }, null);
        coordinator.BindInitialHarnessSubscription();

        await coordinator.RefreshAfterPossibleSessionChangeAsync(CancellationToken.None);
        appContext.Dispatcher.PumpPosted();

        Assert.Same(runtime, sessionContext.CurrentRuntime); // unchanged
    }
}
