using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

public static class ToolExecutionView
{
    public static IReadOnlyList<string> Render(TuiTranscriptItem item, bool expanded, int width = 100)
        => RenderRows(item, expanded, width).Select(row => row.Text.TrimEnd()).ToArray();

    public static IReadOnlyList<TuiChatRow> RenderRows(TuiTranscriptItem item, bool expanded, int width = 100, bool interactive = false)
    {
        width = Math.Max(1, width);
        var name = string.IsNullOrWhiteSpace(item.ToolName) ? "tool" : item.ToolName;
        var kind = item.IsError ? TuiChatRowKind.ToolFailed : item.IsStreaming ? TuiChatRowKind.ToolRunning : TuiChatRowKind.ToolSucceeded;
        var itemExpanded = expanded || item.IsExpanded;
        var target = interactive && !string.IsNullOrWhiteSpace(item.ToolCallId)
            ? new TuiInteractionTarget("tool", item.ToolCallId, Action: "toggle")
            : null;
        var affordance = target is not null ? itemExpanded ? "▾ " : "▸ " : string.Empty;

        if (HasRenderedToolLines(item))
        {
            return WithVerticalPadding(
                BuildRenderedToolLines(affordance, name, item, itemExpanded)
                    .SelectMany(line => TuiAnsiText.WrapLine(line, width))
                    .Select(line => TuiAnsiText.Row(line, kind, width, target))
                    .ToArray(),
                kind,
                width,
                target);
        }

        if (!itemExpanded)
        {
            if (ToolDiffFormatter.TryBuildEditCollapsedLines(affordance, name, item, out var editLines))
            {
                return WithVerticalPadding(
                    editLines
                        .SelectMany(line => TuiAnsiText.WrapLine(line, width))
                        .Select(line => TuiAnsiText.Row(line, kind, width, target))
                        .ToArray(),
                    kind,
                    width,
                    target);
            }

            return WithVerticalPadding(TuiAnsiText.Rows(FormatCollapsed(affordance, name, item), kind, width, target), kind, width, target);
        }

        return WithVerticalPadding(
            BuildExpandedRenderLines(affordance, name, item)
                .SelectMany(line => TuiAnsiText.WrapLine(line, width))
                .Select(line => TuiAnsiText.Row(line, kind, width, target))
                .ToArray(),
            kind,
            width,
            target);
    }

    private static IReadOnlyList<TuiChatRow> WithVerticalPadding(IReadOnlyList<TuiChatRow> rows, TuiChatRowKind kind, int width, TuiInteractionTarget? target)
        => [new TuiChatRow(string.Empty, kind, target).PadTo(width), .. rows, new TuiChatRow(string.Empty, kind, target).PadTo(width)];

    private static bool HasRenderedToolLines(TuiTranscriptItem item)
        => item.RenderedToolCall is { Count: > 0 } || item.RenderedToolResult is { Count: > 0 };

    private static IReadOnlyList<TuiRenderLine> BuildRenderedToolLines(string affordance, string name, TuiTranscriptItem item, bool expanded)
    {
        var lines = new List<TuiRenderLine>();
        lines.AddRange((item.RenderedToolCall ?? []).Select(line => TuiAnsiText.ParseLine(line)));
        if (expanded) lines.AddRange((item.RenderedToolResult ?? []).Select(line => TuiAnsiText.ParseLine(line)));
        if (lines.Count == 0) lines.Add(TuiAnsiText.ParseLine(FormatCollapsed(affordance, name, item)));
        else if (!string.IsNullOrEmpty(affordance)) lines[0] = TuiAnsiText.Prefix(lines[0], affordance);
        return lines;
    }

    private static string FormatCollapsed(string affordance, string name, TuiTranscriptItem item)
    {
        var parts = new List<string> { affordance + name };
        var args = ToolArgumentFormatter.Format(item.ToolArguments, indented: false);
        if (!string.IsNullOrWhiteSpace(args)) parts.Add(args);

        var summary = Summary(item);
        if (!string.IsNullOrWhiteSpace(summary)) parts.Add("→ " + summary);
        return string.Join(' ', parts);
    }

    private static IReadOnlyList<TuiRenderLine> BuildExpandedRenderLines(string affordance, string name, TuiTranscriptItem item)
    {
        if (ToolDiffFormatter.TryBuildEditExpandedLines(affordance, name, item, out var editLines)) return editLines;

        return BuildExpandedLines(affordance, name, item)
            .Select(line => TuiAnsiText.ParseLine(line))
            .ToArray();
    }

    private static IReadOnlyList<string> BuildExpandedLines(string affordance, string name, TuiTranscriptItem item)
    {
        var lines = new List<string> { affordance + name };
        var args = ToolArgumentFormatter.Format(item.ToolArguments, indented: true);
        if (!string.IsNullOrWhiteSpace(args)) lines.AddRange(args.Split('\n'));
        if (!string.IsNullOrWhiteSpace(item.Text)) lines.AddRange(item.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        return lines;
    }

    private static string Summary(TuiTranscriptItem item)
    {
        if (item.IsStreaming) return "running";
        if (item.IsError) return "failed";
        if (string.IsNullOrWhiteSpace(item.Text)) return string.Empty;

        var lineCount = CountLines(item.Text);
        if (string.Equals(item.ToolName, "read", StringComparison.OrdinalIgnoreCase))
        {
            return lineCount == 1 ? "1 line" : $"{lineCount} lines";
        }

        if (IsListLikeTool(item.ToolName))
        {
            return lineCount == 1 ? "1 item" : $"{lineCount} items";
        }

        return lineCount == 1 ? "completed with 1 line" : $"completed with {lineCount} lines";
    }

    private static bool IsListLikeTool(string? toolName)
    {
        return string.Equals(toolName, "find", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "ls", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "grep", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
}
