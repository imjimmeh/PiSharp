namespace PiSharp.Tui.Interactive.Rendering;

internal readonly record struct TuiTextRange(int Offset, int Length);

internal static class TuiTextWrapping
{
    public static IReadOnlyList<TuiTextRange> WrapRanges(string text, int width)
    {
        width = Math.Max(1, width);
        if (string.IsNullOrEmpty(text)) return [new TuiTextRange(0, 0)];

        var ranges = new List<TuiTextRange>();
        var offset = 0;
        while (text.Length - offset > width)
        {
            var split = FindWrapSplit(text, offset, width);
            var length = TrimEndLength(text, offset, split - offset);
            ranges.Add(new TuiTextRange(offset, length));
            offset = SkipLeadingSpaces(text, split);
        }

        ranges.Add(new TuiTextRange(offset, text.Length - offset));
        return ranges;
    }

    private static int FindWrapSplit(string text, int offset, int width)
    {
        var remaining = text.Length - offset;
        var searchEnd = offset + Math.Min(width, remaining - 1);
        var searchCount = searchEnd - offset + 1;
        var split = text.LastIndexOf(' ', searchEnd, searchCount);
        return split <= offset || TrimEndLength(text, offset, split - offset) == 0
            ? offset + width
            : split;
    }

    private static int TrimEndLength(string text, int offset, int length)
    {
        while (length > 0 && text[offset + length - 1] == ' ') length--;
        return length;
    }

    private static int SkipLeadingSpaces(string text, int offset)
    {
        while (offset < text.Length && text[offset] == ' ') offset++;
        return offset;
    }
}
