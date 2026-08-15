namespace PiSharp.Tui.Interactive;

/// <summary>
/// Single thread-safe owner of the immutable <see cref="TuiRenderState"/>. All production
/// mutations route through this type so off-thread reduce/write paths never race the UI render
/// thread. Rendering is NOT scheduled here; callers keep calling the render coordinator.
/// </summary>
internal sealed class RenderStateStore
{
    private readonly object _gate = new();
    private TuiRenderState _state;

    public RenderStateStore(TuiRenderState initial)
    {
        _state = initial;
    }

    public TuiRenderState Snapshot()
    {
        lock (_gate) return _state;
    }

    public void Replace(TuiRenderState next)
    {
        lock (_gate) _state = next;
    }

    public TuiRenderState Update(Func<TuiRenderState, TuiRenderState> update)
    {
        lock (_gate)
        {
            _state = update(_state);
            return _state;
        }
    }
}
