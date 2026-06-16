using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

internal static class ToolDiffFormatter
{
    private const int CollapsedDiffPreviewLineLimit = 10;

    public static bool TryBuildEditCollapsedLines(
        string affordance,
        string name,
        TuiTranscriptItem item,
        out IReadOnlyList<TuiRenderLine> lines)
    {
        lines = [];
        if (!TryGetEditDiff(item, out var diff)) return false;

        var diffLines = SplitDiff(diff);
        var output = BuildEditHeaderLines(affordance, name, item);
        output.AddRange(diffLines.Take(CollapsedDiffPreviewLineLimit).Select(FormatDiffLine));
        if (diffLines.Length > CollapsedDiffPreviewLineLimit)
        {
            output.Add(TuiRenderLine.FromText(
                $"... {diffLines.Length} lines changed, showing {CollapsedDiffPreviewLineLimit}",
                TuiSpanKind.Muted));
        }

        lines = output;
        return true;
    }

    public static bool TryBuildEditExpandedLines(
        string affordance,
        string name,
        TuiTranscriptItem item,
        out IReadOnlyList<TuiRenderLine> lines)
    {
        lines = [];
        if (!TryGetEditDiff(item, out var diff)) return false;

        var output = BuildEditHeaderLines(affordance, name, item);
        output.AddRange(SplitDiff(diff).Select(FormatDiffLine));
        lines = output;
        return true;
    }

    private static List<TuiRenderLine> BuildEditHeaderLines(string affordance, string name, TuiTranscriptItem item)
    {
        var output = new List<TuiRenderLine> { TuiAnsiText.ParseLine(affordance + name) };
        var args = ToolArgumentFormatter.Format(item.ToolArguments, indented: true);
        if (!string.IsNullOrWhiteSpace(args)) output.AddRange(args.Split('\n').Select(line => TuiAnsiText.ParseLine(line)));
        if (!string.IsNullOrWhiteSpace(item.Text)) output.Add(TuiRenderLine.FromText("✓ " + SingleLine(item.Text), TuiSpanKind.Success));
        return output;
    }

    private static string[] SplitDiff(string diff)
        => diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static bool TryGetEditDiff(TuiTranscriptItem item, out string diff)
    {
        diff = string.Empty;
        return string.Equals(item.ToolName, "edit", StringComparison.OrdinalIgnoreCase)
            && TryGetToolDiff(item.ToolResult, out diff);
    }

    private static TuiRenderLine FormatDiffLine(string line)
        => line.StartsWith('+')
            ? TuiRenderLine.FromText(line, TuiSpanKind.Success)
            : line.StartsWith('-')
                ? TuiRenderLine.FromText(line, TuiSpanKind.Error)
                : line.StartsWith("@@", StringComparison.Ordinal)
                    ? TuiRenderLine.FromText(line, TuiSpanKind.Heading)
                    : TuiRenderLine.FromText(line, TuiSpanKind.Muted);

    private static bool TryGetToolDiff(object? toolResult, out string diff)
    {
        diff = string.Empty;
        var details = toolResult?.GetType().GetProperty("Details")?.GetValue(toolResult);
        var value = details?.GetType().GetProperty("Diff")?.GetValue(details) as string;
        if (string.IsNullOrWhiteSpace(value)) return false;
        diff = value;
        return true;
    }

    private static string SingleLine(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
}
