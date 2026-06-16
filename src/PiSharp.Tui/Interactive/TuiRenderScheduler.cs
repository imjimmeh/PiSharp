namespace PiSharp.Tui.Interactive;

public sealed class TuiRenderScheduler
{
    private readonly Action<Action> _dispatch;
    private readonly Action<Action, CancellationToken> _scheduleFrame;
    private readonly TimeSpan _frameInterval;
    private readonly bool _deferFrames;
    private int _renderPending;

    public TuiRenderScheduler(
        Action<Action> dispatch,
        TimeSpan? frameInterval = null,
        Action<Action, CancellationToken>? scheduleFrame = null)
    {
        _dispatch = dispatch;
        _frameInterval = frameInterval ?? TuiTimingOptions.Default.RenderFrameInterval;
        _deferFrames = scheduleFrame is not null || frameInterval is not null;
        _scheduleFrame = scheduleFrame ?? ScheduleFrameAfterDelay;
    }

    public TuiRenderScheduler(
        ITuiDispatcher dispatcher,
        TimeSpan? frameInterval = null,
        Action<Action, CancellationToken>? scheduleFrame = null)
        : this(dispatcher.Post, frameInterval, scheduleFrame)
    {
    }

    public void RequestRender(Action render, CancellationToken cancellationToken = default)
    {
        if (!_deferFrames)
        {
            _dispatch(() =>
            {
                if (!cancellationToken.IsCancellationRequested) render();
            });
            return;
        }

        if (Interlocked.Exchange(ref _renderPending, 1) != 0) return;

        _scheduleFrame(() => _dispatch(() =>
        {
            Interlocked.Exchange(ref _renderPending, 0);
            if (!cancellationToken.IsCancellationRequested) render();
        }), cancellationToken);
    }

    private void ScheduleFrameAfterDelay(Action action, CancellationToken cancellationToken)
    {
        _ = Task.Delay(_frameInterval, cancellationToken).ContinueWith(_ => action(), CancellationToken.None);
    }
}
