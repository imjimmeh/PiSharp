using System.Net;

namespace PiSharp.Research.Web;

/// <summary>
/// Structured metadata parsed from an arxiv abstract page's Highwire citation
/// meta tags. All fields are optional — the page may omit any of them.
/// </summary>
public sealed record ArxivMetadata(
    string? Title,
    IReadOnlyList<string> Authors,
    string? Date,
    string? Abstract,
    string? PdfUrl);

/// <summary>
/// Minimal, regex-free scanner over the small <c>&lt;head&gt;</c> region of an
/// arxiv abstract page: extracts <c>&lt;meta name="citation_*" content="…"&gt;</c>
/// values. Tolerates single/double quotes, whitespace around <c>=</c>, and
/// malformed markup (returns partial results, never throws).
/// </summary>
public static class ArxivMetadataParser
{
    public static ArxivMetadata Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var authors = new List<string>();
        string? title = null;
        string? date = null;
        string? abstractText = null;
        string? pdfUrl = null;

        var scanFrom = 0;
        while (true)
        {
            var metaStart = html.IndexOf("<meta", scanFrom, StringComparison.OrdinalIgnoreCase);
            if (metaStart < 0) break;
            var tagEnd = html.IndexOf('>', metaStart + 5);
            if (tagEnd < 0) break;
            var tag = html.Substring(metaStart, tagEnd - metaStart + 1);
            scanFrom = tagEnd + 1;

            var name = GetAttribute(tag, "name");
            if (name is null || !name.StartsWith("citation_", StringComparison.OrdinalIgnoreCase)) continue;
            var content = GetAttribute(tag, "content");
            if (content is null) continue;

            switch (name.ToLowerInvariant())
            {
                case "citation_title":
                    title ??= content;
                    break;
                case "citation_author":
                    authors.Add(content);
                    break;
                case "citation_date":
                    date ??= content;
                    break;
                case "citation_abstract":
                    abstractText ??= content;
                    break;
                case "citation_pdf_url":
                    pdfUrl ??= content;
                    break;
            }
        }

        return new ArxivMetadata(title, authors, date, abstractText, pdfUrl);
    }

    private static string? GetAttribute(string tag, string attributeName)
    {
        var cursor = 0;
        while (true)
        {
            var attrIndex = tag.IndexOf(attributeName, cursor, StringComparison.OrdinalIgnoreCase);
            if (attrIndex < 0) return null;
            cursor = attrIndex + attributeName.Length;

            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor])) cursor++;
            if (cursor >= tag.Length || tag[cursor] != '=')
            {
                // "name" appeared as a prefix of another attribute; keep scanning.
                continue;
            }

            cursor++;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor])) cursor++;
            if (cursor >= tag.Length) return null;

            var quote = tag[cursor];
            if (quote is '"' or '\'')
            {
                cursor++;
                var valueStart = cursor;
                while (cursor < tag.Length && tag[cursor] != quote) cursor++;
                if (cursor >= tag.Length) return null;
                return WebUtility.HtmlDecode(tag[valueStart..cursor]);
            }

            var unquotedStart = cursor;
            while (cursor < tag.Length && !char.IsWhiteSpace(tag[cursor]) && tag[cursor] != '>') cursor++;
            return WebUtility.HtmlDecode(tag[unquotedStart..cursor]);
        }
    }
}
