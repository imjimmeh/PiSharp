using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public abstract class WidthReflowView : View
{
    private readonly int _fallbackWidth;
    private Action? _reflow;
    private int _renderWidth;

    protected WidthReflowView(int fallbackWidth)
    {
        _fallbackWidth = Math.Max(1, fallbackWidth);
        FrameChanged += (_, _) => ReflowIfWidthChanged();
    }

    protected int RenderWidth => _renderWidth;

    protected int TrackRenderWidth(Action reflow, int? widthOverride = null)
    {
        var width = widthOverride is > 0 ? widthOverride.Value : TuiViewSizing.ResolveWidth(this, _fallbackWidth);
        _renderWidth = width;
        _reflow = widthOverride is > 0 ? null : reflow;
        return width;
    }

    private void ReflowIfWidthChanged()
    {
        if (_reflow is null) return;

        var width = TuiViewSizing.ResolveWidth(this, _fallbackWidth);
        if (width == _renderWidth) return;

        _reflow();
    }
}
