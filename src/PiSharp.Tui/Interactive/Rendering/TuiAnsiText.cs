namespace PiSharp.Tui.Interactive.Rendering;

public static class TuiAnsiText
{
    public static TuiRenderLine ParseLine(string? text, TuiSpanKind fallbackKind = TuiSpanKind.Text)
        => AnsiStyledText.Parse(text ?? string.Empty, defaultSpanKind: fallbackKind).ToRenderLine();

    public static IReadOnlyList<TuiRenderLine> ParseLines(string? text, TuiSpanKind fallbackKind = TuiSpanKind.Text)
        => SplitLines(text).Select(line => ParseLine(line, fallbackKind)).ToArray();

    public static IReadOnlyList<TuiRenderLine> Wrap(string? text, int width, TuiSpanKind fallbackKind = TuiSpanKind.Text)
        => ParseLines(text, fallbackKind).SelectMany(line => WrapLine(line, width)).ToArray();

    public static IReadOnlyList<TuiRenderLine> WrapLine(TuiRenderLine line, int width)
    {
        width = Math.Max(1, width);
        if (line.Text.Length == 0) return [line];

        return TuiTextWrapping.WrapRanges(line.Text, width)
            .Select(range => SliceLine(line, range.Offset, range.Length))
            .ToArray();
    }

    public static TuiRenderLine Prefix(TuiRenderLine line, string prefix, TuiSpanKind prefixKind = TuiSpanKind.Text)
        => new([new TuiSpan(prefix, prefixKind), .. line.Spans]);

    public static TuiChatRow Row(TuiRenderLine line, TuiChatRowKind kind, int width, TuiInteractionTarget? target = null)
        => new TuiChatRow(line.Text, kind, target, line.Spans).PadTo(width);

    public static IReadOnlyList<TuiChatRow> Rows(string? text, TuiChatRowKind kind, int width, TuiInteractionTarget? target = null, TuiSpanKind fallbackKind = TuiSpanKind.Text)
        => Wrap(text, width, fallbackKind).Select(line => Row(line, kind, width, target)).ToArray();

    private static IEnumerable<string> SplitLines(string? text)
        => (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static TuiRenderLine SliceLine(TuiRenderLine line, int offset, int length)
    {
        var spans = new List<TuiSpan>();
        var spanStart = 0;
        foreach (var span in line.Spans)
        {
            var spanEnd = spanStart + span.Text.Length;
            var takeStart = Math.Max(offset, spanStart);
            var takeEnd = Math.Min(offset + length, spanEnd);
            if (takeEnd > takeStart)
            {
                spans.Add(span with { Text = span.Text[(takeStart - spanStart)..(takeEnd - spanStart)] });
            }

            spanStart = spanEnd;
        }

        return new TuiRenderLine(spans);
    }
}
