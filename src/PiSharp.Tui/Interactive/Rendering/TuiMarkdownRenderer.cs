using System.Text.RegularExpressions;

namespace PiSharp.Tui.Interactive.Rendering;

public static partial class TuiMarkdownRenderer
{
    public static IReadOnlyList<TuiRenderLine> Render(string markdown, int width = 100)
    {
        var buffer = new TuiRenderBuffer();
        var inCode = false;
        foreach (var raw in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                buffer.Add(inCode ? "┌─ code" : "└────", TuiSpanKind.Border);
                continue;
            }

            if (inCode)
            {
                foreach (var wrapped in TuiAnsiText.Wrap("│ " + line, width, TuiSpanKind.Code)) buffer.Add(wrapped);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                buffer.Add();
                continue;
            }

            var kind = TuiSpanKind.Text;
            var normalized = line.TrimStart();
            if (normalized.StartsWith('#'))
            {
                normalized = normalized.TrimStart('#').Trim();
                kind = TuiSpanKind.Heading;
            }
            else if (BulletRegex().IsMatch(normalized))
            {
                normalized = BulletRegex().Replace(normalized, "• ");
            }
            else if (NumberedRegex().IsMatch(normalized))
            {
                normalized = NumberedRegex().Replace(normalized, match => $"{match.Groups[1].Value}. ");
            }

            var hasInlineCode = InlineCodeRegex().IsMatch(normalized);
            var hasLink = LinkRegex().IsMatch(normalized);
            normalized = InlineCodeRegex().Replace(normalized, "`$1`");
            normalized = LinkRegex().Replace(normalized, "$1 <$2>");
            if (hasLink) kind = TuiSpanKind.Link;
            else if (hasInlineCode) kind = TuiSpanKind.Code;
            foreach (var wrapped in TuiAnsiText.Wrap(normalized, width, kind)) buffer.Add(wrapped);
        }
        return buffer.Lines;
    }

    public static string ToPlainText(string markdown, int width = 100)
        => string.Join(Environment.NewLine, Render(markdown, width).Select(line => line.Text)).TrimEnd();

    [GeneratedRegex("^[-*+]\\s+")]
    private static partial Regex BulletRegex();

    [GeneratedRegex("^(\\d+)\\.\\s+")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex("\\[([^\\]]+)\\]\\(([^\\)]+)\\)")]
    private static partial Regex LinkRegex();
}
