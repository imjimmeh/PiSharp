namespace PiSharp.Tui.Interactive.Components;

public sealed class WorkingIndicatorView : WrappedTextView
{
    public const string DefaultMessage = "Working...";

    private static readonly string[] DefaultFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public WorkingIndicatorView() : base(fallbackWidth: 100)
    {
        CanFocus = false;
    }

    public void Render(TuiRenderState state, int frameIndex)
    {
        var indicator = state.WorkingIndicator;
        var visible = state.IsBusy && state.WorkingVisible && (indicator?.Visible ?? true);
        Visible = visible;
        if (!visible)
        {
            Text = string.Empty;
            return;
        }

        var message = indicator?.Message ?? state.WorkingMessage ?? DefaultMessage;
        var spinner = indicator?.Spinner ?? DefaultFrames[Math.Abs(frameIndex) % DefaultFrames.Length];
        var text = string.IsNullOrEmpty(spinner) ? message : $"{spinner} {message}";
        RenderWrapped([text], () => Render(state, frameIndex));
    }
}
