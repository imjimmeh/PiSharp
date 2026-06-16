using PiSharp.Tui.Interactive.Shell;

namespace PiSharp.Tui.Interactive;

/// <summary>
/// Single entry point for reading/mutating <see cref="TuiRenderState"/>, requesting a render,
/// and marshalling work onto the Terminal.Gui UI thread.
/// </summary>
internal sealed class TuiStateGateway(
    Func<TuiRenderState> getState,
    Action<TuiRenderState> setState,
    TuiRenderCoordinator renderCoordinator,
    ITuiApplicationContext appContext,
    CancellationToken cancellationToken)
{
    internal TuiRenderState State => getState();

    internal void Set(TuiRenderState next)
    {
        setState(next);
        renderCoordinator.RequestRender();
    }

    internal void Update(Func<TuiRenderState, TuiRenderState> update)
        => Set(update(getState()));

    internal async Task RunOnTuiAsync(Action action)
    {
        try
        {
            await appContext.InvokeAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
