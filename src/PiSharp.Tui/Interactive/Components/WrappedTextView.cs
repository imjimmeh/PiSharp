using PiSharp.Tui.Interactive.Rendering;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public abstract class WrappedTextView : View
{
    private readonly int _fallbackWidth;
    private Action? _reflow;
    private int _renderWidth;

    public IReadOnlyList<AnsiStyledRun> StyledRuns { get; private set; } = [];

    protected WrappedTextView(int fallbackWidth, int initialHeight = 1)
    {
        _fallbackWidth = Math.Max(1, fallbackWidth);
        CanFocus = false;
        Height = Math.Max(1, initialHeight);
        FrameChanged += (_, _) => ReflowIfWidthChanged();
    }

    protected int RenderWidth => _renderWidth;

    protected IReadOnlyList<string> RenderWrapped(IEnumerable<string> lines, Action reflow, int? widthOverride = null)
    {
        var width = widthOverride is > 0 ? widthOverride.Value : TuiViewSizing.ResolveWidth(this, _fallbackWidth);
        _renderWidth = width;
        _reflow = widthOverride is > 0 ? null : reflow;

        var wrappedLines = lines
            .Select(line => AnsiStyledText.Parse(line ?? string.Empty))
            .SelectMany(line => line.Wrap(width))
            .ToArray();
        var wrappedText = wrappedLines.Select(line => line.Text).ToArray();
        StyledRuns = BuildRuns(wrappedLines);
        Height = Math.Max(1, wrappedText.Length);
        Text = string.Join('\n', wrappedText);
        return wrappedText;
    }

    protected override bool OnDrawingText(DrawContext? context)
    {
        if (StyledRuns.Count == 0) return base.OnDrawingText(context);

        foreach (var run in StyledRuns)
        {
            SetAttribute(run.Attribute);
            Move(run.Column, run.Row);
            AddStr(run.Text);
        }

        return true;
    }

    private static IReadOnlyList<AnsiStyledRun> BuildRuns(IReadOnlyList<AnsiStyledText> lines)
    {
        var output = new List<AnsiStyledRun>();
        for (var row = 0; row < lines.Count; row++)
        {
            var column = 0;
            foreach (var run in lines[row].Runs)
            {
                output.Add(new AnsiStyledRun(run.Text, run.Attribute, row, column));
                column += run.Text.Length;
            }
        }

        return output;
    }

    private void ReflowIfWidthChanged()
    {
        if (_reflow is null) return;

        var width = TuiViewSizing.ResolveWidth(this, _fallbackWidth);
        if (width == _renderWidth) return;

        _reflow();
    }
}
