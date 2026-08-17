using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    CancellationToken cancellationToken,
    Func<Func<TuiRenderState, TuiRenderState>, TuiRenderState>? updateState = null,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = loggerFactory?.CreateLogger("TuiStateGateway") ?? NullLogger.Instance;
    private readonly Func<Func<TuiRenderState, TuiRenderState>, TuiRenderState> _updateState =
        updateState ?? (update =>
        {
            var next = update(getState());
            setState(next);
            return next;
        });

    internal TuiRenderState State => getState();

    internal void Set(TuiRenderState next)
    {
        setState(next);
        renderCoordinator.RequestRender();
    }

    internal void Update(Func<TuiRenderState, TuiRenderState> update)
    {
        _updateState(update);
        renderCoordinator.RequestRender();
    }

    internal async Task RunOnTuiAsync(Action action)
    {
        _logger.LogDebug("TuiStateGateway.RunOnTuiAsync entry");
        try
        {
            await appContext.InvokeAsync(() =>
            {
                _logger.LogDebug("TuiStateGateway.RunOnTuiAsync action executing");
                action();
                _logger.LogDebug("TuiStateGateway.RunOnTuiAsync action completed");
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("TuiStateGateway.RunOnTuiAsync exit");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("TuiStateGateway.RunOnTuiAsync cancelled");
        }
    }
}
