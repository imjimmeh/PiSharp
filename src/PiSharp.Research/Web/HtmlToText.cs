using System.Net;
using System.Text;

namespace PiSharp.Research.Web;

/// <summary>
/// Deliberately thin HTML→plain-text conversion for the <c>read https://…</c>
/// affordance: <c>&lt;title&gt;</c> + body text with scripts/styles removed and
/// tags stripped to whitespace. Not a browser — just enough that arbitrary web
/// pages are readable as text.
/// </summary>
public static class HtmlToText
{
    private static readonly string[] BlockTags =
    [
        "p", "div", "br", "li", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "pre",
        "ul", "ol", "table", "blockquote", "section", "article", "header", "footer",
    ];

    /// <summary>
    /// Converts HTML to plain text: title line, then body text with scripts and
    /// styles removed. Output is capped at <paramref name="maxCharacters"/>.
    /// </summary>
    public static string Convert(string html, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (maxCharacters <= 0) return string.Empty;

        var title = ExtractTitle(html);
        var body = StripTags(RemoveScriptAndStyle(html));

        var builder = new StringBuilder(Math.Min(html.Length, maxCharacters + 256));
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("Title: ").Append(title.Trim()).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(body);
        }

        var text = CollapseWhitespace(builder.ToString());
        return text.Length <= maxCharacters ? text : text[..maxCharacters];
    }

    private static string? ExtractTitle(string html)
    {
        var start = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var openEnd = html.IndexOf('>', start);
        if (openEnd < 0) return null;
        var close = html.IndexOf("</title", openEnd, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return null;
        var title = WebUtility.HtmlDecode(html[(openEnd + 1)..close]);
        return string.IsNullOrWhiteSpace(title) ? null : CollapseWhitespace(title).Trim();
    }

    private static string RemoveScriptAndStyle(string html)
    {
        var builder = new StringBuilder(html.Length);
        var cursor = 0;
        while (cursor < html.Length)
        {
            var open = html.IndexOf('<', cursor);
            if (open < 0)
            {
                builder.Append(html, cursor, html.Length - cursor);
                break;
            }

            var nameStart = open + 1;
            while (nameStart < html.Length && char.IsWhiteSpace(html[nameStart])) nameStart++;
            var nameEnd = nameStart;
            while (nameEnd < html.Length && (char.IsAsciiLetter(html[nameEnd]) || char.IsDigit(html[nameEnd]))) nameEnd++;
            var name = html.AsSpan(nameStart, nameEnd - nameStart).ToString().ToLowerInvariant();

            if (name is "script" or "style")
            {
                var close = html.IndexOf($"</{name}", open, StringComparison.OrdinalIgnoreCase);
                if (close < 0)
                {
                    builder.Append(html, cursor, open - cursor);
                    break;
                }

                var closeEnd = html.IndexOf('>', close);
                cursor = closeEnd < 0 ? html.Length : closeEnd + 1;
                continue;
            }

            builder.Append(html, cursor, open - cursor + 1);
            cursor = open + 1;
        }

        return builder.ToString();
    }

    private static string StripTags(string html)
    {
        var builder = new StringBuilder(html.Length);
        var cursor = 0;
        while (cursor < html.Length)
        {
            var open = html.IndexOf('<', cursor);
            if (open < 0)
            {
                builder.Append(html, cursor, html.Length - cursor);
                break;
            }

            builder.Append(html, cursor, open - cursor);
            var close = html.IndexOf('>', open);
            if (close < 0)
            {
                builder.Append(html, open, html.Length - open);
                break;
            }

            var tagName = TagName(html, open, close);
            if (BlockTags.Contains(tagName)) builder.Append('\n');
            cursor = close + 1;
        }

        return WebUtility.HtmlDecode(builder.ToString());
    }

    private static string TagName(string html, int open, int close)
    {
        var nameStart = open + 1;
        if (nameStart < close && html[nameStart] == '/') nameStart++;
        while (nameStart < close && char.IsWhiteSpace(html[nameStart])) nameStart++;
        var nameEnd = nameStart;
        while (nameEnd < close && (char.IsAsciiLetter(html[nameEnd]) || char.IsDigit(html[nameEnd]))) nameEnd++;
        return html.AsSpan(nameStart, nameEnd - nameStart).ToString().ToLowerInvariant();
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingNewlines = 0;
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' or ' ')
            {
                if (ch is '\r' or '\n')
                {
                    if (pendingNewlines < 2) pendingNewlines++;
                    pendingSpace = false;
                }
                else if (pendingNewlines == 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingNewlines > 0)
            {
                builder.Append('\n', pendingNewlines);
                pendingNewlines = 0;
                pendingSpace = false;
            }
            else if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}
