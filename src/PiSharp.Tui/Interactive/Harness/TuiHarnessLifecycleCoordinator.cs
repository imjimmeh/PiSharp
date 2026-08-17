using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Sessions;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.Shell;

namespace PiSharp.Tui.Interactive.Harness;

internal sealed class TuiHarnessLifecycleCoordinator(
    TuiSessionContext session,
    TuiHostOptions options,
    ITuiApplicationContext appContext,
    TuiRenderCoordinator renderCoordinator,
    RenderStateStore store,
    Func<CancellationToken, Task<TuiSessionSnapshot?>> loadSessionSnapshotAsync,
    Action<TuiSessionSnapshot, bool> applySessionSnapshot,
    ILoggerFactory? loggerFactory)
{
    internal TuiHarnessSubscription HarnessSubscription { get; private set; } = null!;

    internal async Task RefreshAfterPossibleSessionChangeAsync(CancellationToken token)
    {
        var snapshot = await loadSessionSnapshotAsync(token);
        appContext.Post(() =>
        {
            if (snapshot is not null) applySessionSnapshot(snapshot, true);
            renderCoordinator.RequestRender(token);
        });
    }

    internal void BindInitialHarnessSubscription()
    {
        HarnessSubscription = CreateHarnessSubscription();
        HarnessSubscription.Bind();
    }

    private void RebindHarnessSubscription()
    {
        HarnessSubscription.Dispose();
        HarnessSubscription = CreateHarnessSubscription();
        HarnessSubscription.Bind();
    }

    private TuiHarnessSubscription CreateHarnessSubscription()
        => new(
            () => session.CurrentRuntime,
            store,
            token => renderCoordinator.RequestRender(token),
            appContext.Post,
            options.ResolveTool,
            options.TimingOptions?.HarnessEventBatchInterval,
            loggerFactory,
            isAbortPending: () => session.AbortPending);
}
