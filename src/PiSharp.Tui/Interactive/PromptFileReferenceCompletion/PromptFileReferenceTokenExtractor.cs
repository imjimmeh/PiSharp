namespace PiSharp.Tui.Interactive;

internal static class PromptFileReferenceTokenExtractor
{
    private static readonly HashSet<char> TokenDelimiters = [' ', '\t', '\n', '\r', '\'', '"', '='];

    internal static bool TryExtractAtPrefix(string text, int cursorOffset, out string prefix, out string rawQuery, out bool quoted)
    {
        prefix = string.Empty;
        rawQuery = string.Empty;
        quoted = false;
        cursorOffset = Math.Clamp(cursorOffset, 0, text.Length);
        var beforeCursor = text[..cursorOffset];

        var quotedAt = beforeCursor.LastIndexOf("@\"", StringComparison.Ordinal);
        if (quotedAt >= 0 && IsTokenStart(beforeCursor, quotedAt))
        {
            var afterOpenQuote = quotedAt + 2;
            var closingQuote = beforeCursor.IndexOf('"', afterOpenQuote);
            if (closingQuote < 0)
            {
                prefix = beforeCursor[quotedAt..];
                rawQuery = beforeCursor[afterOpenQuote..];
                quoted = true;
                return true;
            }
        }

        var tokenStart = FindTokenStart(beforeCursor);
        if (tokenStart < beforeCursor.Length && beforeCursor[tokenStart] == '@')
        {
            prefix = beforeCursor[tokenStart..];
            rawQuery = prefix[1..];
            quoted = false;
            return true;
        }

        return false;
    }

    private static int FindTokenStart(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (TokenDelimiters.Contains(text[i])) return i + 1;
        }
        return 0;
    }

    private static bool IsTokenStart(string text, int index)
        => index == 0 || TokenDelimiters.Contains(text[index - 1]);
}
