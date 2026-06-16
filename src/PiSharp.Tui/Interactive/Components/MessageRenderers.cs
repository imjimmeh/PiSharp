using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Messages;
using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

public static class TuiMessageRenderer
{
    private delegate IReadOnlyList<TuiChatRow> BuiltInMessageRenderer(TuiTranscriptItem item, TuiRenderState state, int width);

    private static readonly IReadOnlyDictionary<string, BuiltInMessageRenderer> BuiltInRenderers =
        new Dictionary<string, BuiltInMessageRenderer>(StringComparer.Ordinal)
        {
            ["user"] = RenderUserRole,
            ["assistant"] = RenderAssistantRole,
            ["tool"] = RenderToolRole,
            ["toolResult"] = RenderToolResultRole,
            ["custom"] = RenderCustomRole,
            ["system"] = RenderSystemRole,
        };

    public static IReadOnlyList<TuiChatRow> Render(TuiTranscriptItem item, TuiRenderState state, int width = 100)
    {
        width = Math.Max(1, width);
        if (item.Role is null) return RenderUnknownRole(item, width);
        return BuiltInRenderers.TryGetValue(item.Role, out var renderer)
            ? renderer(item, state, width)
            : RenderUnknownRole(item, width);
    }

    private static IReadOnlyList<TuiChatRow> RenderUserRole(TuiTranscriptItem item, TuiRenderState state, int width)
        => RenderUserOrSkillInvocation(item.Text, state, width);

    private static IReadOnlyList<TuiChatRow> RenderAssistantRole(TuiTranscriptItem item, TuiRenderState state, int width)
        => RenderAssistant(item, state, width);

    private static IReadOnlyList<TuiChatRow> RenderToolRole(TuiTranscriptItem item, TuiRenderState state, int width)
        => ToolExecutionView.RenderRows(item, state.ShowToolOutput, width, interactive: true);

    private static IReadOnlyList<TuiChatRow> RenderToolResultRole(TuiTranscriptItem item, TuiRenderState state, int width)
        => ToolExecutionView.RenderRows(item, state.ShowToolOutput, width, interactive: false);

    private static IReadOnlyList<TuiChatRow> RenderCustomRole(TuiTranscriptItem item, TuiRenderState state, int width)
    {
        var title = string.IsNullOrWhiteSpace(item.CustomType) ? "[custom]" : $"[{item.CustomType}]";
        var body = string.IsNullOrWhiteSpace(item.Text)
            ? CustomMessageContent.ToDisplayText(item.CustomContent)
            : item.Text;
        return TuiPanelRenderer.Render(title, [body], TuiChatRowKind.Custom, width);
    }

    private static IReadOnlyList<TuiChatRow> RenderSystemRole(TuiTranscriptItem item, TuiRenderState state, int width)
        => TuiAnsiText.Rows($"[{(item.IsError ? "error" : "system")}] {item.Text}",
            item.IsError ? TuiChatRowKind.Error : TuiChatRowKind.System, width);

    private static IReadOnlyList<TuiChatRow> RenderUnknownRole(TuiTranscriptItem item, int width)
        => TuiAnsiText.Rows($"{item.Role}: {item.Text}", TuiChatRowKind.Normal, width);

    private static IReadOnlyList<TuiChatRow> RenderUserOrSkillInvocation(string text, TuiRenderState state, int width)
    {
        if (!TuiSkillInvocationParser.TryParse(text, out var invocation) || invocation is null) return RenderUser(text, width);

        var rows = new List<TuiChatRow>();
        rows.AddRange(RenderSkillInvocation(invocation, state, width));
        if (!string.IsNullOrWhiteSpace(invocation.UserMessage)) rows.AddRange(RenderUser(invocation.UserMessage, width));
        return rows;
    }

    private static IReadOnlyList<TuiChatRow> RenderSkillInvocation(TuiParsedSkillInvocation invocation, TuiRenderState state, int width)
    {
        if (!state.ShowToolOutput)
        {
            var keyHint = TuiKeybindings.HintText("tools", "Ctrl+O");
            var line = new TuiRenderLine(
                new TuiSpan("[skill] ", TuiSpanKind.Accent),
                new TuiSpan(invocation.Name),
                new TuiSpan($" ({keyHint} to expand)", TuiSpanKind.Muted));
            return TuiPanelRenderer.Render("Skill", [line], TuiChatRowKind.Custom, width);
        }

        var innerWidth = Math.Max(1, width - 4);
        var lines = new List<TuiRenderLine>
        {
            new(new TuiSpan(invocation.Name, TuiSpanKind.Heading)),
            TuiRenderLine.FromText(string.Empty)
        };
        lines.AddRange(TuiMarkdownRenderer.Render(invocation.Content, innerWidth));
        return TuiPanelRenderer.Render("Skill", lines, TuiChatRowKind.Custom, width);
    }

    private static IReadOnlyList<TuiChatRow> RenderUser(string text, int width)
    {
        var innerWidth = Math.Max(1, width - 4);
        var lines = TuiAnsiText.Wrap(text, innerWidth);
        var output = new List<TuiChatRow>
        {
            new(string.Empty, TuiChatRowKind.User),
            new("┌" + new string('─', Math.Max(0, width - 2)) + "┐", TuiChatRowKind.User)
        };
        output.AddRange(lines.Select(line => RenderUserLine(line, innerWidth, width)));
        output.Add(new TuiChatRow("└" + new string('─', Math.Max(0, width - 2)) + "┘", TuiChatRowKind.User));
        output.Add(new TuiChatRow(string.Empty, TuiChatRowKind.User));
        return output.Select(row => row.PadTo(width)).ToArray();
    }

    private static IReadOnlyList<TuiChatRow> RenderAssistant(TuiTranscriptItem item, TuiRenderState state, int width)
    {
        if (item.IsError && !string.IsNullOrWhiteSpace(item.Text)) return PrefixError(TuiMarkdownRenderer.Render(item.Text, width), width);
        if (item.Content is null || item.Content.Count == 0)
        {
            var markdownLines = TuiMarkdownRenderer.Render(item.Text, Math.Max(1, width - 4));
            return TuiPanelRenderer.Render("Assistant", markdownLines, TuiChatRowKind.Assistant, width);
        }

        var output = new List<TuiChatRow>();
        var assistantLines = new List<TuiRenderLine>();

        void FlushAssistantBlock()
        {
            if (assistantLines.Count == 0) return;
            output.AddRange(TuiPanelRenderer.Render("Assistant", assistantLines, TuiChatRowKind.Assistant, width));
            assistantLines.Clear();
        }

        foreach (var content in item.Content)
        {
            switch (content)
            {
                case TextContent text:
                    assistantLines.AddRange(TuiMarkdownRenderer.Render(text.Text, Math.Max(1, width - 4)));
                    break;
                case ThinkingContent thinking:
                    assistantLines.Add(TuiRenderLine.FromText(string.Empty));
                    if (state.ShowThinking)
                    {
                        assistantLines.Add(TuiAnsiText.ParseLine("┆ Thinking", TuiSpanKind.Muted));
                        if (thinking.Redacted)
                        {
                            assistantLines.Add(TuiAnsiText.ParseLine("┆ [redacted]", TuiSpanKind.Muted));
                        }
                        else
                        {
                            var thinkingWrapWidth = Math.Max(1, width - 6);
                            foreach (var line in TuiAnsiText.Wrap(thinking.Thinking, thinkingWrapWidth, TuiSpanKind.Muted))
                            {
                                assistantLines.Add(TuiAnsiText.Prefix(line, "┆ ", TuiSpanKind.Muted));
                            }
                        }
                    }
                    else
                    {
                        var hintText = "Thinking hidden (toggle with " + TuiKeybindings.HintText("thinking", "Ctrl+T") + ")";
                        assistantLines.Add(TuiAnsiText.ParseLine(hintText, TuiSpanKind.Muted));
                    }
                    break;
                case ToolCallContent:
                    FlushAssistantBlock();
                    break;
                case ImageContent image:
                    assistantLines.Add(TuiRenderLine.FromText("image: " + image.MediaType));
                    break;
            }
        }
        FlushAssistantBlock();
        return output;
    }

    private static TuiChatRow RenderUserLine(TuiRenderLine line, int innerWidth, int width)
    {
        var padding = new string(' ', Math.Max(0, innerWidth - line.Text.Length));
        var rowText = "│ " + line.Text + padding + " │";
        var spans = new List<TuiSpan> { new("│ ", TuiSpanKind.Border) };
        spans.AddRange(line.Spans);
        if (padding.Length > 0) spans.Add(new TuiSpan(padding));
        spans.Add(new TuiSpan(" │", TuiSpanKind.Border));
        return new TuiChatRow(rowText, TuiChatRowKind.User, Spans: spans).PadTo(width);
    }

    private static IReadOnlyList<TuiChatRow> PrefixError(IEnumerable<TuiRenderLine> lines, int width)
    {
        var output = lines.ToArray();
        if (output.Length == 0) return [new TuiChatRow("✗ Provider error", TuiChatRowKind.Error).PadTo(width)];
        output[0] = TuiAnsiText.Prefix(output[0], "✗ ");
        return output.Select(line => TuiAnsiText.Row(line, TuiChatRowKind.Error, width)).ToArray();
    }

}
