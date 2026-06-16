namespace PiSharp.Tui.Interactive;

public sealed record TuiTimingOptions(
    TimeSpan RenderFrameInterval,
    TimeSpan HarnessEventBatchInterval)
{
    public static TuiTimingOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(8),
        TimeSpan.FromMilliseconds(8));
}
