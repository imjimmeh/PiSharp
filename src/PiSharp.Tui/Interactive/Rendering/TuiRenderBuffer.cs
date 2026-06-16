namespace PiSharp.Tui.Interactive.Rendering;

public sealed class TuiRenderBuffer
{
    private readonly List<TuiRenderLine> _lines = [];

    public IReadOnlyList<TuiRenderLine> Lines => _lines;

    public TuiRenderBuffer Add(string text = "", TuiSpanKind kind = TuiSpanKind.Text)
    {
        _lines.Add(TuiRenderLine.FromText(text, kind));
        return this;
    }

    public TuiRenderBuffer Add(TuiRenderLine line)
    {
        _lines.Add(line);
        return this;
    }

    public TuiRenderBuffer AddRange(IEnumerable<TuiRenderLine> lines)
    {
        _lines.AddRange(lines);
        return this;
    }

    public string ToPlainText() => string.Join(Environment.NewLine, _lines.Select(line => line.Text));

    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        if (string.IsNullOrEmpty(text)) return [string.Empty];

        var output = new List<string>();
        foreach (var sourceLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var offset = 0;
            while (sourceLine.Length - offset > width)
            {
                var searchEnd = offset + Math.Min(width, sourceLine.Length - offset - 1);
                var split = sourceLine.LastIndexOf(' ', searchEnd, searchEnd - offset + 1);
                var splitOffset = split > offset ? split : offset + width;

                var length = splitOffset - offset;
                while (length > 0 && sourceLine[offset + length - 1] == ' ') length--;
                output.Add(sourceLine[offset..(offset + length)]);

                offset = splitOffset;
                while (offset < sourceLine.Length && sourceLine[offset] == ' ') offset++;
            }
            output.Add(sourceLine[offset..]);
        }
        return output;
    }

    public static string Truncate(string text, int width)
    {
        if (width <= 0) return string.Empty;
        if (text.Length <= width) return text;
        return width == 1 ? "…" : text[..(width - 1)] + "…";
    }
}
