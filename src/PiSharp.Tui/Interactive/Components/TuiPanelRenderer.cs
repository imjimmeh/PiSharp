using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

public static class TuiPanelRenderer
{
    public static IReadOnlyList<TuiChatRow> Render(
        string title,
        IEnumerable<string> lines,
        TuiChatRowKind kind,
        int width,
        TuiInteractionTarget? target = null)
        => RenderLines(title, lines.Select(line => TuiAnsiText.ParseLine(line)), kind, width, target, linesAreWrapped: false);

    public static IReadOnlyList<TuiChatRow> Render(
        string title,
        IEnumerable<TuiRenderLine> lines,
        TuiChatRowKind kind,
        int width,
        TuiInteractionTarget? target = null)
        => RenderLines(title, lines, kind, width, target, linesAreWrapped: true);

    private static IReadOnlyList<TuiChatRow> RenderLines(
        string title,
        IEnumerable<TuiRenderLine> lines,
        TuiChatRowKind kind,
        int width,
        TuiInteractionTarget? target,
        bool linesAreWrapped)
    {
        var safeWidth = Math.Max(1, width);
        var innerWidth = Math.Max(1, safeWidth - 4);
        var label = PanelLabel(title, safeWidth);
        var top = "╭" + label + new string('─', Math.Max(0, safeWidth - 2 - label.Length)) + "╮";
        var bottom = "╰" + new string('─', Math.Max(0, safeWidth - 2)) + "╯";

        var rows = new List<TuiChatRow>
        {
            new TuiChatRow(string.Empty, kind, target).PadTo(safeWidth),
            new TuiChatRow(top, kind, target, [new TuiSpan(top, TuiSpanKind.Border)]).PadTo(safeWidth)
        };

        foreach (var sourceLine in lines)
        {
            var renderedLines = linesAreWrapped
                ? [sourceLine]
                : TuiAnsiText.WrapLine(sourceLine, innerWidth);

            foreach (var line in renderedLines)
            {
                rows.Add(RenderContentRow(line, kind, target, innerWidth, safeWidth));
            }
        }

        rows.Add(new TuiChatRow(bottom, kind, target, [new TuiSpan(bottom, TuiSpanKind.Border)]).PadTo(safeWidth));
        rows.Add(new TuiChatRow(string.Empty, kind, target).PadTo(safeWidth));
        return rows;
    }

    private static TuiChatRow RenderContentRow(TuiRenderLine line, TuiChatRowKind kind, TuiInteractionTarget? target, int innerWidth, int safeWidth)
    {
        var text = TuiRenderBuffer.Truncate(line.Text, innerWidth);
        var padding = new string(' ', Math.Max(0, innerWidth - text.Length));
        var rowText = "│ " + text + padding + " │";
        var spans = new List<TuiSpan> { new("│ ", TuiSpanKind.Border) };
        spans.AddRange(TrimSpans(line.Spans, text.Length));
        if (padding.Length > 0) spans.Add(new TuiSpan(padding));
        spans.Add(new TuiSpan(" │", TuiSpanKind.Border));
        return new TuiChatRow(rowText, kind, target, spans).PadTo(safeWidth);
    }

    private static IReadOnlyList<TuiSpan> TrimSpans(IReadOnlyList<TuiSpan> spans, int length)
    {
        var remaining = length;
        var output = new List<TuiSpan>();
        foreach (var span in spans)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, span.Text.Length);
            output.Add(span with { Text = span.Text[..take] });
            remaining -= take;
        }

        return output;
    }

    private static string PanelLabel(string title, int width)
    {
        var available = Math.Max(0, width - 2);
        var label = "─ " + title + " ";
        if (label.Length <= available) return label;
        return available > 0 ? "─" : string.Empty;
    }
}
