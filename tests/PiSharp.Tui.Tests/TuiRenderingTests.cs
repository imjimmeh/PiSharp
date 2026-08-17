using Microsoft.Extensions.Logging;
using System.Drawing;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Rendering;
using PiSharp.Tui.Interactive.Shell;
using PiSharp.Tui.Interactive.Theme;
using PiSharp.Tui.Tests.TestLogging;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiRenderingTests
{
    [Fact]
    public void RenderBufferWrapsAndTruncatesText()
    {
        Assert.Equal(["hello", "world"], TuiRenderBuffer.Wrap("hello world", 5));
        Assert.Equal("abcd…", TuiRenderBuffer.Truncate("abcdef", 5));
    }

    [Fact]
    public void RenderBufferWrapEmptyAndNullReturnsOneEmptyRow()
    {
        Assert.Equal([string.Empty], TuiRenderBuffer.Wrap("", 5));
        Assert.Equal([string.Empty], TuiRenderBuffer.Wrap(null!, 5));
    }

    [Fact]
    public void RenderBufferWrapNormalizesCrlfToLfBehavior()
    {
        var lf = TuiRenderBuffer.Wrap("hello\nworld", 5);
        var crlf = TuiRenderBuffer.Wrap("hello\r\nworld", 5);

        Assert.Equal(lf, crlf);
        Assert.Equal(["hello", "world"], lf);
    }

    [Fact]
    public void RenderBufferWrapChunksLongUnbrokenWordsAtWidth()
    {
        Assert.Equal(["abc", "def", "ghi"], TuiRenderBuffer.Wrap("abcdefghi", 3));
        Assert.Equal(["ab", "cd", "ef"], TuiRenderBuffer.Wrap("abcdef", 2));
    }

    [Fact]
    public void RenderBufferWrapTrimsTrailingSpacesAtSplitPoints()
    {
        Assert.Equal(["hello", "world"], TuiRenderBuffer.Wrap("hello   world", 5));
    }

    [Fact]
    public void RenderBufferWrapTrimsLeadingSpacesAfterSplitPoints()
    {
        Assert.Equal(["hello", "world"], TuiRenderBuffer.Wrap("hello       world", 5));
    }

    [Fact]
    public void RenderBufferWrapPreservesMultipleSourceLines()
    {
        Assert.Equal(["line1", "line2"], TuiRenderBuffer.Wrap("line1\nline2", 10));
        Assert.Equal(["a", "b", "c"], TuiRenderBuffer.Wrap("a\nb\nc", 10));
    }

    [Fact]
    public void RenderBufferWrapHandlesLineAtExactWidth()
    {
        Assert.Equal(["abcde"], TuiRenderBuffer.Wrap("abcde", 5));
        Assert.Equal(["hello", "world"], TuiRenderBuffer.Wrap("hello\nworld", 5));
    }

    [Fact]
    public void RenderBufferWrapHandlesWidthOfOne()
    {
        Assert.Equal([string.Empty], TuiRenderBuffer.Wrap("", 1));
        Assert.Equal(["a", "b"], TuiRenderBuffer.Wrap("ab", 1));
    }

    [Fact]
    public void AnsiTextWrapDoesNotEmitEmptyRowsAtWhitespaceBoundaries()
    {
        var leading = TuiMarkdownRenderer.Render("```\n  indented words\n```", 4).Select(line => line.Text).ToArray();
        var boundary = TuiMarkdownRenderer.Render("```\nword     next\n```", 6).Select(line => line.Text).ToArray();

        Assert.DoesNotContain(string.Empty, leading);
        Assert.DoesNotContain(string.Empty, boundary);
        Assert.Contains(boundary, line => line.Contains("next", StringComparison.Ordinal));
    }

    [Fact]
    public void ChatRowPadToPreservesInteractionTarget()
    {
        var target = new TuiInteractionTarget("tool", "tool-1", SourceId: "core", Action: "toggle");
        var row = new TuiChatRow("tool", TuiChatRowKind.ToolRunning, target).PadTo(20);

        Assert.Equal(20, row.Text.Length);
        Assert.Same(target, row.InteractionTarget);
        Assert.Equal("tool", row.InteractionTarget?.Kind);
        Assert.Equal("tool-1", row.InteractionTarget?.Id);
    }

    [Fact]
    public void MarkdownRendererNormalizesHeadingsBulletsCodeAndLinks()
    {
        var text = TuiMarkdownRenderer.ToPlainText("# Title\n- one\n[pi](https://example.test)\n```\ncode\n```");

        Assert.Contains("Title", text);
        Assert.Contains("• one", text);
        Assert.Contains("pi <https://example.test>", text);
        Assert.Contains("│ code", text);
    }

    [Fact]
    public void MessageRendererPreservesMarkdownSpansForChatRows()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("assistant", "# Title\n[pi](https://example.test)\n```\ncode\n```"), Empty(), 48);

        var heading = Assert.Single(rows, row => row.Text.Contains("Title", StringComparison.Ordinal));
        var link = Assert.Single(rows, row => row.Text.Contains("pi <https://example.test>", StringComparison.Ordinal));
        var code = Assert.Single(rows, row => row.Text.Contains("│ code", StringComparison.Ordinal));

        var headingSpans = heading.GetType().GetProperty("Spans")?.GetValue(heading) as IReadOnlyList<TuiSpan> ?? [];
        var linkSpans = link.GetType().GetProperty("Spans")?.GetValue(link) as IReadOnlyList<TuiSpan> ?? [];
        var codeSpans = code.GetType().GetProperty("Spans")?.GetValue(code) as IReadOnlyList<TuiSpan> ?? [];

        Assert.Contains(headingSpans, span => span.Kind == TuiSpanKind.Heading && span.Text.Contains("Title", StringComparison.Ordinal));
        Assert.Contains(linkSpans, span => span.Kind == TuiSpanKind.Link && span.Text.Contains("pi <https://example.test>", StringComparison.Ordinal));
        Assert.Contains(codeSpans, span => span.Kind == TuiSpanKind.Code && span.Text.Contains("│ code", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultKeybindingsIncludeOriginalLikeShortcuts()
    {
        var keys = TuiKeybindings.Defaults.Select(binding => binding.Keys).ToArray();

        Assert.Contains("Ctrl+L", keys);
        Assert.Contains("Ctrl+O", keys);
        Assert.Contains("Ctrl+T", keys);
        Assert.Contains("Ctrl+Y", keys);
        Assert.Contains("Ctrl+H", keys);
        Assert.Contains("Esc", keys);
        Assert.Contains("Ctrl+D", keys);
    }

    [Fact]
    public void ThemeExposesSemanticTokensForParitySurfaces()
    {
        Assert.Equal("#1e1a12", TuiTheme.GetTokenHex(TuiThemeToken.UserMessageBackground));
        Assert.Equal("#1c1a16", TuiTheme.GetTokenHex(TuiThemeToken.ToolPendingBackground));
        Assert.Equal(new Terminal.Gui.Color(0xe8, 0xe0, 0xd5), TuiTheme.GetTokenAttribute(TuiThemeToken.UserMessageText).Foreground);
        Assert.Equal(new Terminal.Gui.Color(0x1e, 0x1a, 0x12), TuiTheme.GetTokenAttribute(TuiThemeToken.UserMessageText).Background);
        Assert.NotEqual(TuiTheme.DefaultColorScheme.Normal, TuiTheme.GetTokenAttribute(TuiThemeToken.ToolErrorBackground));
    }

    [Fact]
    public void UserMessageBorderSpansPreserveUserMessageBackground()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("user", "hello"), Empty(), 24);
        var contentRow = Assert.Single(rows, row => row.Text.Contains("hello", StringComparison.Ordinal));
        var userAttribute = TuiTheme.ChatRowAttribute(TuiChatRowKind.User);

        Assert.Equal(TuiSpanKind.Border, contentRow.Spans![0].Kind);
        Assert.Equal(TuiSpanKind.Border, contentRow.Spans[^1].Kind);
        Assert.Equal(userAttribute.Background, TuiTheme.SpanAttribute(contentRow.Spans[0].Kind, userAttribute).Background);
        Assert.Equal(userAttribute.Foreground, TuiTheme.SpanAttribute(contentRow.Spans[0].Kind, userAttribute).Foreground);
        Assert.Equal(userAttribute.Background, TuiTheme.SpanAttribute(contentRow.Spans[^1].Kind, userAttribute).Background);
        Assert.Equal(userAttribute.Foreground, TuiTheme.SpanAttribute(contentRow.Spans[^1].Kind, userAttribute).Foreground);
    }

    [Fact]
    public void MutedSpansOnColoredRowsUseReadableForeground()
    {
        var userAttribute = TuiTheme.ChatRowAttribute(TuiChatRowKind.User);
        var toolAttribute = TuiTheme.ChatRowAttribute(TuiChatRowKind.ToolSucceeded);

        var userMuted = TuiTheme.SpanAttribute(TuiSpanKind.Muted, userAttribute);
        var toolBorder = TuiTheme.SpanAttribute(TuiSpanKind.Border, toolAttribute);

        Assert.Equal(userAttribute.Background, userMuted.Background);
        Assert.Equal(userAttribute.Foreground, userMuted.Foreground);
        Assert.Equal(toolAttribute.Background, toolBorder.Background);
        Assert.Equal(toolAttribute.Foreground, toolBorder.Foreground);
    }

    [Fact]
    public void DefaultThemeStylesMarkdownSpans()
    {
        var fallback = TuiTheme.ChatRowAttribute(TuiChatRowKind.Assistant);

        Assert.NotEqual(fallback, TuiTheme.SpanAttribute(TuiSpanKind.Heading, fallback));
        Assert.NotEqual(fallback, TuiTheme.SpanAttribute(TuiSpanKind.Link, fallback));
        Assert.NotEqual(fallback, TuiTheme.SpanAttribute(TuiSpanKind.Code, fallback));
        Assert.NotEqual(fallback, TuiTheme.SpanAttribute(TuiSpanKind.Border, fallback));
    }

    [Fact]
    public void ThemeApplyUpdatesActiveThemeNameAndTokenLookup()
    {
        try
        {
            TuiTheme.Apply(new TuiThemeDocument("Dim Team", new Dictionary<string, string> { ["accent"] = "#ffffff" }));

            Assert.Equal("Dim Team", TuiTheme.ActiveThemeName);
            Assert.Equal("#ffffff", TuiTheme.GetTokenHex(TuiThemeToken.Accent));
        }
        finally
        {
            TuiTheme.ApplyDefault();
        }
    }

    [Fact]
    public void AnsiStyledTextParserMapsExtensionThemeColors()
    {
        var styled = AnsiStyledText.Parse("\u001b[32mLSP\u001b[39m \u001b[31mInactive\u001b[39m");

        Assert.Equal("LSP Inactive", styled.Text);
        Assert.Collection(styled.Runs,
            run =>
            {
                Assert.Equal("LSP", run.Text);
                Assert.Equal(TuiTheme.GetTokenAttribute(TuiThemeToken.Success), run.Attribute);
            },
            run =>
            {
                Assert.Equal(" ", run.Text);
                Assert.Equal(TuiTheme.GetTokenAttribute(TuiThemeToken.Text), run.Attribute);
            },
            run =>
            {
                Assert.Equal("Inactive", run.Text);
                Assert.Equal(TuiTheme.GetTokenAttribute(TuiThemeToken.Error), run.Attribute);
            });
    }

    [Fact]
    public void FooterStatusRenderingPreservesAnsiStyleRuns()
    {
        var footer = new FooterView();
        var state = Empty() with
        {
            ExtensionStatuses = new Dictionary<string, string> { ["pi-lens-lsp"] = "\u001b[31mLSP Inactive\u001b[39m" }
        };

        footer.Render(state, new TuiFooterSnapshot("cwd", null, 0, 0, 0, 0, 0, 0, 0, false, state.Statuses), widthOverride: 80);

        Assert.Contains("LSP Inactive", footer.Text?.ToString() ?? string.Empty);
        Assert.Contains(footer.StyledRuns, run => run.Text == "LSP Inactive" && run.Attribute == TuiTheme.GetTokenAttribute(TuiThemeToken.Error));
    }

    [Fact]
    public void FooterPlainExtensionStatusUsesBrightText()
    {
        var footer = new FooterView();
        var state = Empty() with
        {
            ExtensionStatuses = new Dictionary<string, string> { ["glm-usage"] = "GLM Lite | 5h: 16%" }
        };

        footer.Render(state, new TuiFooterSnapshot("cwd", null, 0, 0, 0, 0, 0, 0, 0, false, state.Statuses), widthOverride: 80);

        Assert.Contains("GLM Lite", footer.Text?.ToString() ?? string.Empty);
        Assert.Contains(footer.StyledRuns, run => run.Text == "GLM Lite | 5h: 16%" && run.Attribute == AnsiStyledText.BrightTextAttribute);
    }

    [Fact]
    public void FooterRenderingStripsAnsiAndKeepsStyleRuns()
    {
        var footer = new FooterView();
        var state = Empty().UpsertBridgeSlot(new TuiBridgeSlot("footer:ext", "text", "Footer", "\u001b[32mLSP\u001b[39m \u001b[31mInactive\u001b[39m", Placement: "footer"));

        footer.Render(state, widthOverride: 80);

        Assert.Equal("LSP Inactive", footer.Text?.ToString());
        Assert.Contains(footer.StyledRuns, run => run.Text == "LSP" && run.Attribute == TuiTheme.GetTokenAttribute(TuiThemeToken.Success));
        Assert.Contains(footer.StyledRuns, run => run.Text == "Inactive" && run.Attribute == TuiTheme.GetTokenAttribute(TuiThemeToken.Error));
    }

    [Fact]
    public void FooterStatusTokenIsErrorAttrWhenStatusIsError()
    {
        var footer = new FooterView();
        footer.Render(Empty() with { Status = "error: failed" }, new TuiFooterSnapshot("repo", "main", 10, 20, 5, 35, 0.123m, 42d, 100, false, new Dictionary<string, string>()));
        Assert.Contains(footer.StyledRuns, run => run.Attribute == TuiTheme.GetTokenAttribute(TuiThemeToken.Error));
    }

    [Fact]
    public void FooterRendersWithoutThrowingWhenExtensionStatusesIsNull()
    {
        var footer = new FooterView();
        var state = Empty();

        // Remote TUI path builds the footer snapshot from ServerFooterSnapshot, which carries no
        // extension statuses, so ExtensionStatuses is null (mirrors ClientToTuiAdapter.ToSessionSnapshot).
        var snapshot = new TuiFooterSnapshot("cwd", null, 0, 0, 0, 0, 0, 0, 0, false);

        footer.Render(state, snapshot, widthOverride: 80);

        Assert.Contains("tools:none", footer.Text?.ToString() ?? string.Empty);
    }

    [Fact]
    public void MessageRendererCoversUserAssistantThinkingImageAndSystemRows()
    {
        var state = Empty();
        var assistant = new TuiTranscriptItem("assistant", string.Empty, Content:
        [
            new TextContent("## Hello\n- item"),
            new ThinkingContent("secret"),
            new ImageContent("image/png", "data")
        ]);

        var user = TuiMessageRenderer.Render(new TuiTranscriptItem("user", "question"), state).Select(row => row.Text).ToArray();
        var hidden = TuiMessageRenderer.Render(assistant, state).Select(row => row.Text).ToArray();
        var visible = TuiMessageRenderer.Render(assistant, state.ToggleThinking()).Select(row => row.Text).ToArray();
        var system = TuiMessageRenderer.Render(new TuiTranscriptItem("system", "boom", IsError: true), state).Select(row => row.Text).ToArray();

        Assert.Contains(user, line => line.Contains("question"));
        Assert.Contains(hidden, line => line.Contains("Thinking hidden", StringComparison.Ordinal));
        Assert.Contains(visible, line => line.Contains("secret"));
        Assert.Contains(hidden, line => line.Contains("image/png"));
        Assert.Contains(system, line => line.Contains("[error] boom", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererRendersUserRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("user", "hello world"), Empty(), 40);

        Assert.All(rows, row => Assert.Equal(40, row.Text.Length));
        Assert.All(rows, row => Assert.Equal(TuiChatRowKind.User, row.Kind));
        Assert.Contains(rows, row => row.Text.Contains("hello world", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("┌", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("└", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererRendersAssistantRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("assistant", "assistant response"), Empty(), 40);

        Assert.All(rows, row => Assert.Equal(40, row.Text.Length));
        Assert.All(rows, row => Assert.Equal(TuiChatRowKind.Assistant, row.Kind));
        Assert.Contains(rows, row => row.Text.Contains("Assistant", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("assistant response", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererRendersToolRole()
    {
        var item = new TuiTranscriptItem("tool", "tool line 1\ntool line 2", ToolName: "read", ToolCallId: "tool-1");
        var rows = TuiMessageRenderer.Render(item, Empty(), 40);

        Assert.True(rows.Count > 1, $"Expected at least 2 tool rows but got {rows.Count}");
        Assert.Contains(rows, row => row.Kind == TuiChatRowKind.ToolRunning || row.Kind == TuiChatRowKind.ToolSucceeded);
        Assert.Contains(rows, row => row.Text.Contains("read", StringComparison.Ordinal));
        var disclosureRow = rows.SingleOrDefault(row => row.Text.Contains("▸", StringComparison.Ordinal));
        Assert.NotNull(disclosureRow);
    }

    [Fact]
    public void BuiltInRendererRendersToolResultRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("toolResult", "result text", ToolName: "read", ToolCallId: "tool-1"), Empty(), 40);

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(40, row.Text.Length));
        Assert.Contains(rows, row => row.Kind == TuiChatRowKind.ToolSucceeded);
        Assert.Contains(rows, row => row.Text.Contains("read", StringComparison.Ordinal));
        Assert.All(rows, row => Assert.DoesNotContain("▸", row.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererRendersCustomRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("custom", "custom content"), Empty(), 40);

        Assert.All(rows, row => Assert.Equal(40, row.Text.Length));
        Assert.All(rows, row => Assert.Equal(TuiChatRowKind.Custom, row.Kind));
        Assert.Contains(rows, row => row.Text.Contains("custom", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("custom content", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererRendersSystemRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("system", "system notice"), Empty(), 40);

        Assert.Contains(rows, row => row.Kind == TuiChatRowKind.System);
        Assert.Contains(rows, row => row.Text.Contains("[system] system notice", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererRendersSystemErrorRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("system", "error notice", IsError: true), Empty(), 40);

        Assert.Contains(rows, row => row.Kind == TuiChatRowKind.Error);
        Assert.Contains(rows, row => row.Text.Contains("[error] error notice", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererFallsBackOnUnknownRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("unknown-role", "fallback text"), Empty(), 40);

        Assert.All(rows, row => Assert.Equal(TuiChatRowKind.Normal, row.Kind));
        Assert.Contains(rows, row => row.Text.Contains("unknown-role: fallback text", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererFallsBackOnCaseMismatchedUserRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("User", "case test"), Empty(), 40);

        Assert.All(rows, row => Assert.Equal(TuiChatRowKind.Normal, row.Kind));
        Assert.Contains(rows, row => row.Text.Contains("User: case test", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Kind == TuiChatRowKind.User);
    }

    [Fact]
    public void BuiltInRendererFallsBackOnCaseMismatchedToolResultRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("ToolResult", "case test"), Empty(), 40);

        Assert.All(rows, row => Assert.Equal(TuiChatRowKind.Normal, row.Kind));
        Assert.Contains(rows, row => row.Text.Contains("ToolResult: case test", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInRendererFallsBackOnNullRole()
    {
        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem(null!, "null role text"), Empty(), 40);

        Assert.All(rows, row => Assert.Equal(TuiChatRowKind.Normal, row.Kind));
        Assert.Contains(rows, row => row.Text.Contains(": null role text", StringComparison.Ordinal));
    }

    [Fact]
    public void MessageRendererStripsAnsiAcrossPlainPanelUserAssistantAndToolRows()
    {
        var state = Empty().ToggleToolOutput();
        var rows = new List<TuiChatRow>();
        rows.AddRange(TuiMessageRenderer.Render(new TuiTranscriptItem("system", "\u001b[36mNotice\u001b[39m"), state, 80));
        rows.AddRange(TuiMessageRenderer.Render(new TuiTranscriptItem("system", "\u001b[?25lCursor\u001b[?25h"), state, 80));
        rows.AddRange(TuiMessageRenderer.Render(new TuiTranscriptItem("custom", "\u001b[31mCustom\u001b[39m"), state, 80));
        rows.AddRange(TuiMessageRenderer.Render(new TuiTranscriptItem("user", "\u001b[32mQuestion\u001b[39m"), state, 80));
        rows.AddRange(TuiMessageRenderer.Render(new TuiTranscriptItem("assistant", "\u001b[33mAnswer\u001b[39m"), state, 80));
        rows.AddRange(TuiMessageRenderer.Render(new TuiTranscriptItem("tool", "\u001b[90mraw result\u001b[39m", ToolName: "shell"), state, 80));
        rows.AddRange(TuiMessageRenderer.Render(new TuiTranscriptItem("extension", "\u001b[36mfallback\u001b[39m"), state, 80));

        var text = string.Join('\n', rows.Select(row => row.Text));
        Assert.Contains("Notice", text, StringComparison.Ordinal);
        Assert.Contains("Cursor", text, StringComparison.Ordinal);
        Assert.Contains("Custom", text, StringComparison.Ordinal);
        Assert.Contains("Question", text, StringComparison.Ordinal);
        Assert.Contains("Answer", text, StringComparison.Ordinal);
        Assert.Contains("raw result", text, StringComparison.Ordinal);
        Assert.Contains("fallback", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[36m", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[?25", text, StringComparison.Ordinal);
        Assert.Contains(rows.SelectMany(row => row.Spans ?? []), span => span.Text.Contains("Notice", StringComparison.Ordinal) && span.Kind == TuiSpanKind.Accent);
    }

    [Fact]
    public void SkillInvocationUserMessagesRenderAsCustomCollapsedPanels()
    {
        var message = "<skill name=\"example\" location=\"/repo/skills/example/SKILL.md\">\nReferences are relative to /repo/skills/example.\n\nFull skill content\n</skill>\n\napply this";

        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("user", message), Empty(), 80);
        var text = string.Join('\n', rows.Select(row => row.Text));

        Assert.Contains(rows, row => row.Kind == TuiChatRowKind.Custom && row.Text.Contains("Skill", StringComparison.Ordinal));
        Assert.Contains("[skill] example", text, StringComparison.Ordinal);
        Assert.Contains("Ctrl+O", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Full skill content", text, StringComparison.Ordinal);
        Assert.Contains(rows, row => row.Kind == TuiChatRowKind.User && row.Text.Contains("apply this", StringComparison.Ordinal));
    }

    [Fact]
    public void SkillInvocationUserMessagesExpandWithSkillContent()
    {
        var message = "<skill name=\"example\" location=\"/repo/skills/example/SKILL.md\">\nReferences are relative to /repo/skills/example.\n\n# Full skill content\n</skill>";

        var rows = TuiMessageRenderer.Render(new TuiTranscriptItem("user", message), Empty().ToggleToolOutput(), 80);
        var text = string.Join('\n', rows.Select(row => row.Text));

        Assert.Contains(rows, row => row.Kind == TuiChatRowKind.Custom);
        Assert.Contains("example", text, StringComparison.Ordinal);
        Assert.Contains("Full skill content", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<skill", text, StringComparison.Ordinal);
        Assert.DoesNotContain("</skill>", text, StringComparison.Ordinal);
        Assert.DoesNotContain(rows, row => row.Kind == TuiChatRowKind.User);
    }

    [Fact]
    public void ToolAndDiffRenderingCoverCollapsedExpandedAndErrors()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"path\":\"a.txt\"}");
        var item = Empty().Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tool-1", "read", "details", false)))
            .Transcript.Single();
        var collapsed = ToolExecutionView.Render(item, false);
        var expanded = ToolExecutionView.Render(item, true);
        var error = ToolExecutionView.RenderRows(new TuiTranscriptItem("tool", "bad", ToolName: "read", IsError: true), false);
        var diff = DiffView.Render("@@\n-old\n+new");

        Assert.Contains("read", collapsed[1]);
        Assert.Contains("a.txt", collapsed[1]);
        Assert.DoesNotContain("details", string.Join('\n', collapsed), StringComparison.Ordinal);
        Assert.Contains("details", string.Join('\n', expanded));
        Assert.Equal(TuiChatRowKind.ToolFailed, error[0].Kind);
        Assert.True(string.IsNullOrWhiteSpace(error[0].Text));
        Assert.Contains(diff, line => line.StartsWith("+ new", StringComparison.Ordinal));
    }

    [Fact]
    public void CollapsedReadToolRowsSummarizeWithoutLeakingContent()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"path\":\"src/file.txt\"}");
        var item = new TuiTranscriptItem("tool", "first line\nsecond line", ToolName: "read", ToolArguments: args.RootElement.Clone());

        var collapsed = ToolExecutionView.Render(item, false, 80);
        var expanded = ToolExecutionView.Render(item, true, 80);

        Assert.Contains("read", collapsed[1]);
        Assert.Contains("src/file.txt", collapsed[1]);
        Assert.Contains("2 lines", collapsed[1]);
        Assert.DoesNotContain("first line", string.Join('\n', collapsed), StringComparison.Ordinal);
        Assert.Contains("first line", string.Join('\n', expanded), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolRowsFormatJsonArgumentsAsReadableKeyValueRows()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"path\":\"src/file.txt\",\"offset\":1,\"options\":{\"caseSensitive\":true}}");
        var item = new TuiTranscriptItem("tool", "first line\nsecond line", ToolName: "read", ToolArguments: args.RootElement.Clone());

        var collapsed = ToolExecutionView.Render(item, false, 100);
        var expanded = ToolExecutionView.Render(item, true, 100);
        var expandedText = string.Join('\n', expanded);

        Assert.Contains("path: src/file.txt", collapsed[1]);
        Assert.Contains("offset: 1", collapsed[1]);
        Assert.DoesNotContain("{\"path\"", collapsed[1], StringComparison.Ordinal);
        Assert.Contains("- path: src/file.txt", expandedText, StringComparison.Ordinal);
        Assert.Contains("- offset: 1", expandedText, StringComparison.Ordinal);
        Assert.Contains("- options: {\"caseSensitive\":true}", expandedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedEditToolRowsRenderResultDiffWithStyledLines()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"path\":\"src/Foo.cs\"}");
        var result = new PiSharp.Agent.Core.Tools.AgentToolResult<object?>(
            [new TextContent("Successfully replaced 1 block(s) in src/Foo.cs.")],
            new { Diff = " 10 before\n-11 old\n+11 new\n 12 after", FirstChangedLine = 11 });
        var item = new TuiTranscriptItem(
            "tool",
            "Successfully replaced 1 block(s) in src/Foo.cs.",
            ToolName: "edit",
            ToolArguments: args.RootElement.Clone(),
            ToolResult: result);

        var rows = ToolExecutionView.RenderRows(item, true, 100);
        var text = string.Join('\n', rows.Select(row => row.Text));

        Assert.Contains("- path: src/Foo.cs", text, StringComparison.Ordinal);
        Assert.Contains("✓ Successfully replaced 1 block(s) in src/Foo.cs.", text, StringComparison.Ordinal);
        Assert.Contains("-11 old", text, StringComparison.Ordinal);
        Assert.Contains("+11 new", text, StringComparison.Ordinal);
        Assert.Contains(rows.SelectMany(row => row.Spans ?? []), span => span.Text.Contains("-11 old", StringComparison.Ordinal) && span.Kind == TuiSpanKind.Error);
        Assert.Contains(rows.SelectMany(row => row.Spans ?? []), span => span.Text.Contains("+11 new", StringComparison.Ordinal) && span.Kind == TuiSpanKind.Success);
    }

    [Fact]
    public void CollapsedEditToolRowsRenderTruncatedDiffPreviewWhileExpandedRowsRenderFullDiff()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"path\":\"src/Foo.cs\"}");
        var diff = string.Join('\n', Enumerable.Range(1, 12).Select(index => index % 2 == 0 ? $"+{index:D2} new" : $"-{index:D2} old"));
        var result = new PiSharp.Agent.Core.Tools.AgentToolResult<object?>(
            [new TextContent("Successfully replaced 1 block(s) in src/Foo.cs.")],
            new { Diff = diff, FirstChangedLine = 1 });
        var item = new TuiTranscriptItem(
            "tool",
            "Successfully replaced 1 block(s) in src/Foo.cs.",
            ToolName: "edit",
            ToolArguments: args.RootElement.Clone(),
            ToolResult: result);

        var collapsedRows = ToolExecutionView.RenderRows(item, false, 100);
        var expandedRows = ToolExecutionView.Render(item, true, 100);
        var collapsedText = string.Join('\n', collapsedRows.Select(row => row.Text));

        Assert.Contains("- path: src/Foo.cs", collapsedText, StringComparison.Ordinal);
        Assert.Contains("✓ Successfully replaced 1 block(s) in src/Foo.cs.", collapsedText, StringComparison.Ordinal);
        Assert.Contains("-01 old", collapsedText, StringComparison.Ordinal);
        Assert.Contains("+10 new", collapsedText, StringComparison.Ordinal);
        Assert.DoesNotContain("-11 old", collapsedText, StringComparison.Ordinal);
        Assert.DoesNotContain("+12 new", collapsedText, StringComparison.Ordinal);
        Assert.Contains("... 12 lines changed, showing 10", collapsedText, StringComparison.Ordinal);
        Assert.Contains(expandedRows, line => line.Contains("-11 old", StringComparison.Ordinal));
        Assert.Contains(expandedRows, line => line.Contains("+12 new", StringComparison.Ordinal));
        Assert.Contains(collapsedRows.SelectMany(row => row.Spans ?? []), span => span.Text.Contains("-01 old", StringComparison.Ordinal) && span.Kind == TuiSpanKind.Error);
        Assert.Contains(collapsedRows.SelectMany(row => row.Spans ?? []), span => span.Text.Contains("+02 new", StringComparison.Ordinal) && span.Kind == TuiSpanKind.Success);
    }

    [Fact]
    public void BuiltInToolRowsSummarizeListLikeResultsByItemCount()
    {
        var find = new TuiTranscriptItem("tool", "src/A.cs\nsrc/B.cs", ToolName: "find");
        var ls = new TuiTranscriptItem("tool", "bin/\nobj/\nfile.txt", ToolName: "ls");

        Assert.Contains("2 items", ToolExecutionView.Render(find, false, 80)[1]);
        Assert.Contains("3 items", ToolExecutionView.Render(ls, false, 80)[1]);
    }

    [Fact]
    public void ToolRowsPreferExtensionRenderedCallAndResultLines()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"action\":\"create\",\"subject\":\"Audit extension styling\"}");
        var item = new TuiTranscriptItem(
            "tool",
            "Created #1: Audit extension styling (pending)",
            ToolName: "todo",
            ToolCallId: "tool-1",
            ToolArguments: args.RootElement.Clone(),
            RenderedToolCall: ["todo + Audit extension styling"],
            RenderedToolResult: ["○ pending"]);

        var collapsed = ToolExecutionView.Render(item, false, 80);
        var expanded = ToolExecutionView.Render(item, true, 80);

        Assert.Equal("todo + Audit extension styling", collapsed[1]);
        Assert.DoesNotContain("○ pending", string.Join('\n', collapsed), StringComparison.Ordinal);
        Assert.Equal("todo + Audit extension styling", expanded[1]);
        Assert.Equal("○ pending", expanded[2]);
        Assert.DoesNotContain("{\"action\"", string.Join('\n', collapsed), StringComparison.Ordinal);
        Assert.DoesNotContain("Created #1", string.Join('\n', collapsed), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolRowsStripAnsiFromExtensionRenderedLinesAndKeepSpans()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"action\":\"create\",\"subject\":\"Test todo tool\"}");
        var item = new TuiTranscriptItem(
            "tool",
            "Created #1: Test todo tool (pending)",
            ToolName: "todo",
            ToolCallId: "tool-1",
            ToolArguments: args.RootElement.Clone(),
            RenderedToolCall: ["\u001b[37m\u001b[1mtodo \u001b[22m\u001b[39m\u001b[90m+\u001b[39m \u001b[90mTest todo tool\u001b[39m"]);

        var rows = ToolExecutionView.RenderRows(item, false, 80, interactive: true);

        Assert.True(string.IsNullOrWhiteSpace(rows[0].Text));
        Assert.Equal("▸ todo + Test todo tool", rows[1].Text.TrimEnd());
        Assert.DoesNotContain("\u001b", rows[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[37m", rows[1].Text, StringComparison.Ordinal);
        Assert.Contains(rows[1].Spans ?? [], span => span.Text == "+" && span.Kind == TuiSpanKind.Muted);
        Assert.Contains(rows[1].Spans ?? [], span => span.Text == "Test todo tool" && span.Kind == TuiSpanKind.Muted);
    }

    [Fact]
    public void ToolRowsExposeInteractionTargetAcrossWholeItem()
    {
        var expanded = ToolExecutionView.RenderRows(new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1", IsExpanded: true), false, 40, interactive: true);
        var collapsed = ToolExecutionView.RenderRows(new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1"), false, 40, interactive: true);

        Assert.Contains("▾", expanded[1].Text);
        Assert.Contains("▸", collapsed[1].Text);
        Assert.Contains("read", collapsed[1].Text);
        Assert.All(expanded, row =>
        {
            Assert.Equal("tool", row.InteractionTarget?.Kind);
            Assert.Equal("tool-1", row.InteractionTarget?.Id);
            Assert.Equal("toggle", row.InteractionTarget?.Action);
        });
    }

    [Fact]
    public void AssistantToolCallContentIsRenderedByToolLifecycleRows()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{}");
        var assistant = new TuiTranscriptItem("assistant", string.Empty, Content:
        [
            new ToolCallContent("call-1", "read", args.RootElement.Clone())
        ]);

        var rows = TuiMessageRenderer.Render(assistant, Empty(), 40);

        Assert.Empty(rows);
    }

    [Fact]
    public void ChatViewRendersBridgeSlotInteractionTargets()
    {
        var target = new TuiInteractionTarget("bridge", "widget:extension-a", SourceId: "extension:a");
        var chat = new ChatView();

        chat.Render(Empty() with
        {
            BridgeSlots = [new TuiBridgeSlot("widget:extension-a", "text", "Widget", "hello", Placement: "above-chat", SourceId: "extension:a", InteractionTarget: target)]
        });

        var row = Assert.Single(chat.Rows, row => row.Text.Contains("Widget", StringComparison.Ordinal));
        Assert.Same(target, row.InteractionTarget);
    }

    [Fact]
    public void ChatViewBridgeSlotsStripAnsiAndSplitMultilineContent()
    {
        var chat = new ChatView();
        chat.Render(Empty() with
        {
            BridgeSlots = [new TuiBridgeSlot("widget:pi-lens", "text", "pi-lens", "\n\n[rpiv-todos] \u001b[36m●\u001b[39m \u001b[36mTodos (0/1)\u001b[39m", Placement: "above-chat", SourceId: "pi-lens")]
        });

        var text = string.Join('\n', chat.Rows.Select(row => row.Text));
        Assert.Contains("[pi-lens]", text, StringComparison.Ordinal);
        Assert.Contains("[rpiv-todos] ● Todos (0/1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[36m", text, StringComparison.Ordinal);
        Assert.Contains(chat.Rows.SelectMany(row => row.Spans ?? []), span => span.Text.Contains("Todos", StringComparison.Ordinal) && span.Kind == TuiSpanKind.Accent);
    }

    [Fact]
    public void ChatViewBridgeSlotsWrapLongAnsiContentWithoutTruncating()
    {
        var chat = new ChatView { Width = 24 };
        chat.Render(Empty() with
        {
            BridgeSlots = [new TuiBridgeSlot("widget:pi-lens", "text", "pi-lens", "[rpiv-todos] \u001b[36mTodos include many words wrapping\u001b[39m", Placement: "above-chat", SourceId: "pi-lens")]
        });

        var text = string.Join('\n', chat.Rows.Select(row => row.Text.TrimEnd()));
        Assert.Contains("wrapping", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.All(chat.Rows, row => Assert.True(row.Text.Length <= 24, $"'{row.Text}' exceeded 24"));
    }

    [Fact]
    public void ChatViewInteractionHitMapsVisibleRowToSourceRow()
    {
        var target = new TuiInteractionTarget("bridge", "widget:extension-a", SourceId: "extension:a");
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 8).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();

        chat.Render(Empty() with
        {
            Transcript = transcript,
            BridgeSlots = [new TuiBridgeSlot("widget:extension-a", "text", "Widget", "hello", SourceId: "extension:a", InteractionTarget: target)]
        });

        var sourceRow = chat.Rows.ToList().FindIndex(row => ReferenceEquals(row.InteractionTarget, target));
        var viewRow = sourceRow - chat.ScrollTop;

        Assert.InRange(viewRow, 0, 2);
        Assert.True(chat.TryGetInteractionHit(viewRow, 5, out var hit));
        Assert.NotNull(hit);
        Assert.Same(target, hit!.Target);
        Assert.Equal(sourceRow, hit.SourceRow);
        Assert.Equal(viewRow, hit.ViewRow);
        Assert.Equal(5, hit.Column);
    }

    [Fact]
    public void ChatViewInteractionHitIgnoresRowsWithoutTargets()
    {
        var chat = new ChatView { Height = 3 };

        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "plain")] });

        Assert.False(chat.TryGetInteractionHit(0, 0, out var hit));
        Assert.Null(hit);
    }

    [Fact]
    public void ChatViewContextHitMapsRowsWithEntryIds()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("user", "hello", EntryId: "entry-1")] });

        Assert.True(chat.TryGetContextHit(0, 2, out var hit));
        Assert.NotNull(hit);
        Assert.Equal("message", hit!.Target.Kind);
        Assert.Equal("entry-1", hit.Target.Id);
        Assert.Equal("user", hit.Target.Data!["role"]);
        Assert.False(chat.TryGetInteractionHit(0, 2, out _));
    }

    [Fact]
    public void ChatViewRightClickRequestsContextMenuWithoutToolToggle()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        TuiInteractionHit? contextHit = null;
        TuiInteractionHit? interactionHit = null;
        chat.ContextMenuRequested += hit => contextHit = hit;
        chat.InteractionRequested += hit => interactionHit = hit;
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1", EntryId: "entry-1")] });

        var args = SendMouse(chat, MouseFlags.Button3Clicked, 2, 0);

        Assert.True(args.Handled);
        Assert.NotNull(contextHit);
        Assert.Equal("entry-1", contextHit!.Target.Id);
        Assert.Null(interactionHit);
    }

    [Fact]
    public void ChatViewStandaloneMouseClickOnToolRowRequestsInteraction()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        TuiInteractionHit? requested = null;
        chat.InteractionRequested += hit => requested = hit;
        chat.Render(Empty() with
        {
            Transcript = [new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1")]
        });

        var args = new MouseEventArgs
        {
            Flags = MouseFlags.Button1Clicked,
            Position = new Point(2, 0),
            ScreenPosition = new Point(2, 0),
            View = chat
        };
        var handled = chat.NewMouseEvent(args);

        Assert.True(handled);
        Assert.True(args.Handled);
        Assert.NotNull(requested);
        Assert.Equal("tool-1", requested!.Target.Id);
        Assert.Equal("toggle", requested.Target.Action);
    }

    [Fact]
    public void ChatViewPressReleaseOnToolRowRequestsInteraction()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        TuiInteractionHit? requested = null;
        chat.InteractionRequested += hit => requested = hit;
        chat.Render(Empty() with
        {
            Transcript = [new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1")]
        });

        SendMouse(chat, MouseFlags.Button1Pressed, 2, 0);
        var release = SendMouse(chat, MouseFlags.Button1Released, 2, 0);

        Assert.True(release.Handled);
        Assert.NotNull(requested);
        Assert.Equal("tool-1", requested!.Target.Id);
        Assert.Equal("toggle", requested.Target.Action);
    }

    [Fact]
    public void ChatViewPressReleaseOnToolRowTogglesRenderedExpansion()
    {
        var chat = new ChatView { Width = 80, Height = 4 };
        var state = Empty() with
        {
            Transcript = [new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1")]
        };
        chat.InteractionRequested += hit =>
        {
            state = state.ToggleToolExpanded(hit.Target.Id);
            chat.Render(state);
        };
        chat.Render(state);

        Assert.DoesNotContain(chat.Rows, row => row.Text.Contains("details", StringComparison.Ordinal));

        SendMouse(chat, MouseFlags.Button1Pressed, 2, 0);
        SendMouse(chat, MouseFlags.Button1Released, 2, 0);
        SendMouse(chat, MouseFlags.Button1Clicked, 2, 0);

        Assert.True(state.Transcript.Single().IsExpanded);
        Assert.Contains(chat.Rows, row => row.Text.Contains("details", StringComparison.Ordinal));

        SendMouse(chat, MouseFlags.Button1Pressed, 2, 0);
        SendMouse(chat, MouseFlags.Button1Released, 2, 0);
        SendMouse(chat, MouseFlags.Button1Clicked, 2, 0);

        Assert.False(state.Transcript.Single().IsExpanded);
        Assert.DoesNotContain(chat.Rows, row => row.Text.Contains("details", StringComparison.Ordinal));
    }

    [Fact]
    public void ChatViewDragFromToolRowDoesNotSelectTextOrRequestInteraction()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        TuiInteractionHit? requested = null;
        chat.InteractionRequested += hit => requested = hit;
        chat.Render(Empty() with
        {
            Transcript = [new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1")]
        });

        SendMouse(chat, MouseFlags.Button1Pressed, 2, 0);
        SendMouse(chat, MouseFlags.Button1Pressed, 6, 0);
        var release = SendMouse(chat, MouseFlags.Button1Released, 6, 0);

        Assert.True(release.Handled);
        Assert.Null(requested);
        Assert.Equal(string.Empty, chat.SelectedText);
    }

    [Fact]
    public void ChatViewSelectionReleaseOverToolRowDoesNotRequestInteraction()
    {
        var chat = new ChatView { Width = 80, Height = 5 };
        TuiInteractionHit? requested = null;
        chat.InteractionRequested += hit => requested = hit;
        chat.Render(Empty() with
        {
            Transcript =
            [
                new TuiTranscriptItem("system", "alpha beta"),
                new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1")
            ]
        });
        var toolRow = chat.Rows.ToList().FindIndex(row => row.InteractionTarget?.Id == "tool-1");

        SendMouse(chat, MouseFlags.Button1Pressed, 9, 0);
        SendMouse(chat, MouseFlags.Button1Pressed, 13, 0);
        var release = SendMouse(chat, MouseFlags.Button1Released, 2, toolRow);
        var click = SendMouse(chat, MouseFlags.Button1Clicked, 2, toolRow);

        Assert.True(release.Handled);
        Assert.True(click.Handled);
        Assert.Null(requested);
    }

    [Fact]
    public void ChatViewMouseDragSelectsTranscriptTextWithoutCopying()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        var copyAttempts = 0;
        chat.ClipboardWriter = text =>
        {
            copyAttempts++;
            return true;
        };
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "alpha beta")] });

        SendMouse(chat, MouseFlags.Button1Pressed, 9, 1);
        SendMouse(chat, MouseFlags.Button1Pressed, 13, 1);
        var release = SendMouse(chat, MouseFlags.Button1Released, 13, 1);

        Assert.True(chat.WantContinuousButtonPressed);
        Assert.True(release.Handled);
        Assert.Equal("alpha", chat.SelectedText);
        Assert.Equal(0, copyAttempts);
    }

    [Fact]
    public void ChatViewCopySelectionToClipboardCopiesSelectedText()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        string? copied = null;
        (string Text, bool Copied)? copiedEvent = null;
        chat.ClipboardWriter = text =>
        {
            copied = text;
            return true;
        };
        chat.SelectionCopied += (text, copiedToClipboard) => copiedEvent = (text, copiedToClipboard);
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "alpha beta")] });
        SendMouse(chat, MouseFlags.Button1Pressed, 9, 1);
        SendMouse(chat, MouseFlags.Button1Pressed, 13, 1);
        SendMouse(chat, MouseFlags.Button1Released, 13, 1);

        Assert.True(chat.CopySelectionToClipboard());
        Assert.Equal("alpha", copied);
        Assert.Equal(("alpha", true), copiedEvent);
        Assert.Equal(string.Empty, chat.SelectedText);
    }

    [Fact]
    public void ChatViewCopySelectionToClipboardReportsFailedCopyAndClearsSelection()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        (string Text, bool Copied)? copiedEvent = null;
        chat.ClipboardWriter = _ => false;
        chat.SelectionCopied += (text, copiedToClipboard) => copiedEvent = (text, copiedToClipboard);
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "alpha beta")] });
        SendMouse(chat, MouseFlags.Button1Pressed, 9, 1);
        SendMouse(chat, MouseFlags.Button1Pressed, 13, 1);
        SendMouse(chat, MouseFlags.Button1Released, 13, 1);

        Assert.False(chat.CopySelectionToClipboard());
        Assert.Equal(("alpha", false), copiedEvent);
        Assert.Equal(string.Empty, chat.SelectedText);
    }

    [Fact]
    public void ChatViewSelectedTextPreservesLineBreaksAcrossRows()
    {
        var chat = new ChatView { Width = 80, Height = 5 };
        chat.TranscriptItemRenderer = (_, _, _) =>
        [
            new TuiChatRow("first line"),
            new TuiChatRow("second line")
        ];
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "ignored")] });

        SendMouse(chat, MouseFlags.Button1Pressed, 0, 1);
        SendMouse(chat, MouseFlags.Button1Pressed, 5, 2);
        SendMouse(chat, MouseFlags.Button1Released, 5, 2);

        Assert.True(chat.HasSelection);
        Assert.Equal($"first line{Environment.NewLine}second", chat.SelectedText);
    }

    [Fact]
    public void ChatViewSelectionDragStaysAnchoredWhenTranscriptRendersWhileAutoScrolled()
    {
        var chat = new ChatView { Width = 80, Height = 10 };
        var transcript = new List<TuiTranscriptItem>
        {
            new("system", "alpha beta"),
            new("system", "gamma delta")
        };
        chat.Render(Empty() with { Transcript = transcript });

        SendMouse(chat, MouseFlags.Button1Pressed, 9, 1);
        SendMouse(chat, MouseFlags.Button1Pressed, 13, 1);
        transcript.Add(new TuiTranscriptItem("assistant", "new streamed output"));
        chat.Render(Empty() with { Transcript = transcript });
        SendMouse(chat, MouseFlags.Button1Pressed, 13, 1);

        Assert.Equal("alpha", chat.SelectedText);
    }

    [Fact]
    public void ChatViewSelectionUsesTerminalCellColumnsForWideCharacters()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "界 alpha")] });

        SendMouse(chat, MouseFlags.Button1Pressed, 12, 1);
        SendMouse(chat, MouseFlags.Button1Pressed, 16, 1);
        SendMouse(chat, MouseFlags.Button1Released, 16, 1);

        Assert.Equal("alpha", chat.SelectedText);
    }

    [Fact]
    public void ChatViewMouseClickWithoutDragDoesNotCopySelection()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        var copyAttempts = 0;
        chat.ClipboardWriter = text =>
        {
            copyAttempts++;
            return true;
        };
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "alpha beta")] });

        SendMouse(chat, MouseFlags.Button1Pressed, 9, 0);
        SendMouse(chat, MouseFlags.Button1Released, 9, 0);

        Assert.Equal(string.Empty, chat.SelectedText);
        Assert.Equal(0, copyAttempts);
    }

    [Fact]
    public void HeaderSupportsCompactAndExpandedModes()
    {
        var header = new HeaderView();
        var state = Empty();

        header.Render(state, expanded: false);
        var compact = header.Text?.ToString() ?? string.Empty;

        header.Render(state, expanded: true);
        var expanded = header.Text?.ToString() ?? string.Empty;

        Assert.Contains("details", compact);
        Assert.Contains("/help", expanded);
        Assert.DoesNotContain("/help", compact);
    }

    [Fact]
    public void HeaderShowsExtensionLoadingIndicator()
    {
        var header = new HeaderView();
        var state = Empty();

        header.Render(state, expanded: false, extensionLoadStatus: new TuiExtensionLoadStatus(Total: 5, Active: 2, BlockingActive: 0, Ready: 2, Failed: 1));

        var text = header.Text?.ToString() ?? string.Empty;
        Assert.Contains("ext loading 3/5", text);
    }

    [Fact]
    public void HeaderShowsExtensionFailureSummaryAfterLoadingCompletes()
    {
        var header = new HeaderView();
        var state = Empty();

        header.Render(state, expanded: false, extensionLoadStatus: new TuiExtensionLoadStatus(Total: 5, Active: 0, BlockingActive: 0, Ready: 4, Failed: 1));

        var text = header.Text?.ToString() ?? string.Empty;
        Assert.Contains("ext ready 4/5 failed 1", text);
    }

    [Fact]
    public void ExtensionLoadCompletedMessageListsFailedExtensions()
    {
        var status = new TuiExtensionLoadStatus(
            Total: 3,
            Active: 0,
            BlockingActive: 0,
            Ready: 1,
            Failed: 2,
            Failures: [
                new("C:/extensions/bad-one.ts", "Cannot read properties of undefined (reading 'display_name')"),
                new("C:/extensions/bad-two.ts", "this.unsubscribe is not a function")
            ]);

        var message = status.FormatCompletedMessage();

        Assert.Contains("Extensions loaded: 1/3 ready, 2 failed.", message);
        Assert.Contains("C:/extensions/bad-one.ts", message);
        Assert.Contains("display_name", message);
        Assert.Contains("C:/extensions/bad-two.ts", message);
        Assert.Contains("this.unsubscribe is not a function", message);
    }

    [Fact]
    public void HeaderLinesNeverExceedFrameWidth()
    {
        var header = new HeaderView { Width = 24 };
        var state = Empty()
            .SetTitle("Very Long PiSharp Title") with
        {
            SessionName = "very-long-session-name",
            ModelDisplay = "provider/very-long-model-name",
            Status = "Thinking"
        };

        header.Render(state, expanded: true);
        var text = header.Text?.ToString() ?? string.Empty;
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.DoesNotContain("…", text);
        Assert.True(lines.Length > 3);
        Assert.All(lines, line => Assert.True(line.Length <= header.Frame.Width, $"'{line}' exceeded {header.Frame.Width}"));
    }

    [Fact]
    public void HeaderStatusTokenIsEmberAccentWhenBusyAndDimWhenIdle()
    {
        var accent = TuiTheme.GetTokenAttribute(TuiThemeToken.Accent);

        var busyHeader = new HeaderView();
        busyHeader.Render(Empty() with { Status = "working", IsBusy = true }, false);
        var busyAccentCount = busyHeader.StyledRuns.Count(r => r.Attribute == accent);
        Assert.True(busyAccentCount > 0, "Busy header should render its status token with the ember accent.");

        var idleHeader = new HeaderView();
        idleHeader.Render(Empty() with { Status = "idle", IsBusy = false }, false);
        var idleAccentCount = idleHeader.StyledRuns.Count(r => r.Attribute == accent);
        Assert.True(idleAccentCount < busyAccentCount, "Idle header should render fewer accent runs than busy.");
    }

    [Fact]
    public void WrappedTextViewsStripAnsiFromExtensionControlledText()
    {
        var state = (Empty() with { IsBusy = true })
            .SetTitle("\u001b[36mPi Lens\u001b[39m")
            .SetWorkingMessage("\u001b[32mCrunching\u001b[39m")
            .SetWorkingIndicator(new ExtensionWorkingIndicator(Spinner: "\u001b[33m●\u001b[39m"));
        var header = new HeaderView();
        var indicator = new WorkingIndicatorView();
        var suggestions = new InlineSuggestionListView();

        header.Render(state, expanded: false);
        indicator.Render(state, 0);
        suggestions.SetSuggestions(["\u001b[36m/search\u001b[39m"]);
        var wrappedIndicator = new WorkingIndicatorView { Width = 12 };
        wrappedIndicator.Render(state.SetWorkingMessage("\u001b[32mLong styled operation wraps\u001b[39m"), 0);

        var text = string.Join('\n', header.Text?.ToString(), indicator.Text?.ToString(), suggestions.Text?.ToString(), wrappedIndicator.Text?.ToString());
        Assert.Contains("Pi Lens", text, StringComparison.Ordinal);
        Assert.Contains("● Crunching", text, StringComparison.Ordinal);
        Assert.Contains("/search", text, StringComparison.Ordinal);
        Assert.Contains("wraps", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[36m", text, StringComparison.Ordinal);
        Assert.Contains(wrappedIndicator.StyledRuns, run => run.Row > 0 && run.Attribute == TuiTheme.GetTokenAttribute(TuiThemeToken.Success));
    }

    [Fact]
    public void HeaderReflowsWhenFrameWidthChanges()
    {
        var header = new HeaderView { Frame = new Rectangle(0, 0, 50, 3) };
        var state = Empty().SetTitle("A very long title that should reflow on resize");
        header.Render(state, expanded: true);

        header.Frame = new Rectangle(0, 0, 18, 8);
        var lines = (header.Text?.ToString() ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r'));

        Assert.All(lines, line => Assert.True(line.Length <= 18, $"'{line}' exceeded 18 after resize"));
    }

    [Fact]
    public void WorkingIndicatorRendersDefaultAndCustomizedBusyStates()
    {
        var indicator = new WorkingIndicatorView();
        var idle = Empty();
        var busy = idle with { IsBusy = true };
        var customized = busy
            .SetWorkingMessage("Crunching...")
            .SetWorkingIndicator(new ExtensionWorkingIndicator(Spinner: "●"));

        indicator.Render(idle, 0);
        Assert.False(indicator.Visible);
        Assert.Equal(string.Empty, indicator.Text?.ToString());

        indicator.Render(busy, 1);
        Assert.True(indicator.Visible);
        Assert.Equal("⠙ Working...", indicator.Text?.ToString());

        indicator.Render(customized, 0);
        Assert.True(indicator.Visible);
        Assert.Equal("● Crunching...", indicator.Text?.ToString());

        indicator.Render(customized.SetWorkingVisible(false), 0);
        Assert.False(indicator.Visible);
    }

    [Fact]
    public void WorkingIndicatorLineNeverExceedsFrameWidth()
    {
        var indicator = new WorkingIndicatorView { Width = 16 };
        var state = Empty() with { IsBusy = true };
        state = state.SetWorkingMessage("Working on a very long operation");

        indicator.Render(state, 0);
        var text = indicator.Text?.ToString() ?? string.Empty;
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.DoesNotContain("…", text);
        Assert.True(lines.Length > 1);
        Assert.All(lines, line => Assert.True(line.Length <= indicator.Frame.Width, $"'{line}' exceeded {indicator.Frame.Width}"));
    }

    [Fact]
    public void WorkingIndicatorRunsAreEmberAccentWhenBusy()
    {
        var indicator = new WorkingIndicatorView();
        var state = (Empty() with { IsBusy = true }).SetWorkingMessage("thinking");
        indicator.Render(state, 0);

        Assert.All(indicator.StyledRuns, run =>
            Assert.Equal(TuiTheme.GetTokenAttribute(TuiThemeToken.Accent), run.Attribute));
        Assert.Contains("thinking", indicator.Text?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FooterSnapshotAggregatesUsageAndVisibleFields()
    {
        var usage = new UsageInfo(Input: 10, Output: 15, CacheRead: 3, CacheWrite: 2, TotalTokens: 30, Cost: new UsageCost(Total: 0.0123m));
        var state = Empty() with
        {
            Transcript = [new TuiTranscriptItem("assistant", "ok", Usage: usage)],
            ContextWindow = 100,
            ExtensionStatuses = new Dictionary<string, string> { ["ext"] = "ready" }
        };

        var snapshot = FooterDataProvider.CreateSnapshot(state, Environment.CurrentDirectory);
        var footer = new FooterView();
        footer.Render(state, snapshot, widthOverride: 80);

        Assert.Equal(30, snapshot.TotalTokens);
        Assert.Equal(5, snapshot.CacheTokens);
        Assert.Equal(30, snapshot.ContextPercent);
        Assert.Contains("ready", footer.Text?.ToString());
    }

    [Fact]
    public void FooterSnapshotProviderLogsCacheRefreshAndHit()
    {
        var usage = new UsageInfo(TotalTokens: 10);
        var state = Empty() with { Transcript = [new TuiTranscriptItem("assistant", "ok", Usage: usage)] };
        var now = DateTimeOffset.UtcNow;
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var snapshotProvider = new TuiFooterSnapshotProvider(
            resolveGitBranch: _ => "main",
            clock: () => now,
            loggerFactory: loggerFactory);

        _ = snapshotProvider.CreateSnapshot(state, "repo");
        _ = snapshotProvider.CreateSnapshot(state, "repo");

        Assert.Contains(provider.Entries, entry => entry.Category.Contains(nameof(TuiFooterSnapshotProvider), StringComparison.Ordinal)
            && entry.Message.Contains("cache refresh", StringComparison.Ordinal)
            && entry.Message.Contains("branch=main", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, entry => entry.Category.Contains(nameof(TuiFooterSnapshotProvider), StringComparison.Ordinal)
            && entry.Message.Contains("cache hit", StringComparison.Ordinal)
            && entry.Message.Contains("branch=main", StringComparison.Ordinal));
    }

    [Fact]
    public void FooterViewLogsRenderedSummary()
    {
        var state = Empty() with
        {
            ModelDisplay = "test/model",
            ThinkingLevel = ThinkingLevel.Low
        };
        var snapshot = new TuiFooterSnapshot("repo", "main", 10, 20, 5, 35, 0.123m, 42d, 100, false, new Dictionary<string, string>());
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var footer = new FooterView(loggerFactory);

        footer.Render(state, snapshot, widthOverride: 80);

        Assert.Contains(provider.Entries, entry => entry.Category.Contains(nameof(FooterView), StringComparison.Ordinal)
            && entry.Message.Contains("model=test/model", StringComparison.Ordinal)
            && entry.Message.Contains("thinking=Low", StringComparison.Ordinal)
            && entry.Message.Contains("branch=main", StringComparison.Ordinal)
            && entry.Message.Contains("width=80", StringComparison.Ordinal));
    }

    [Fact]
    public void FooterViewRendersActiveTools()
    {
        var state = Empty();
        var snapshot = new TuiFooterSnapshot("repo", "main", 10, 20, 5, 35, 0.123m, 42d, 100, false, new Dictionary<string, string>());
        var footer = new FooterView();

        footer.Render(state, snapshot, activeTools: ["read", "write", "grep"], widthOverride: 80);

        var text = footer.Text?.ToString() ?? string.Empty;
        Assert.Contains("tools:read,write,grep", text);
    }


    [Fact]
    public void FooterSnapshotEstimatesContextFromSessionEntriesWhenUsageMissing()
    {
        var state = Empty() with { ContextWindow = 100 };
        var entries = new SessionTreeEntry[]
        {
            new MessageEntry
            {
                Id = "u1",
                ParentId = null,
                Timestamp = DateTimeOffset.UtcNow,
                Message = AgentMessages.User(new string('x', 40))
            }
        };

        var snapshot = FooterDataProvider.CreateSnapshotFromSessionEntries(state, Environment.CurrentDirectory, entries);

        Assert.Equal(0, snapshot.TotalTokens);
        Assert.Equal(10, snapshot.ContextPercent);
    }

    [Fact]
    public void FooterSnapshotUsesLatestAssistantUsagePlusTrailingEstimatedMessages()
    {
        var state = Empty() with { ContextWindow = 100 };
        var usage = new UsageInfo(Input: 20, Output: 30, TotalTokens: 50);
        var entries = new SessionTreeEntry[]
        {
            new MessageEntry
            {
                Id = "u1",
                ParentId = null,
                Timestamp = DateTimeOffset.UtcNow,
                Message = AgentMessages.User(new string('u', 40))
            },
            new MessageEntry
            {
                Id = "a1",
                ParentId = "u1",
                Timestamp = DateTimeOffset.UtcNow,
                Message = new AssistantMessage([new TextContent("ok")], Usage: usage, StopReason: "stop")
            },
            new MessageEntry
            {
                Id = "t1",
                ParentId = "a1",
                Timestamp = DateTimeOffset.UtcNow,
                Message = AgentMessages.ToolResult("tool-1", "read", new string('t', 40))
            }
        };

        var snapshot = FooterDataProvider.CreateSnapshotFromSessionEntries(state, Environment.CurrentDirectory, entries);

        Assert.Equal(50, snapshot.TotalTokens);
        Assert.Equal(60, snapshot.ContextPercent);
    }

    [Fact]
    public void FooterShowsUnknownContextAfterCompactionUntilNewAssistantUsageArrives()
    {
        var state = Empty() with { ContextWindow = 100 };
        var entries = new SessionTreeEntry[]
        {
            new MessageEntry
            {
                Id = "u1",
                ParentId = null,
                Timestamp = DateTimeOffset.UtcNow,
                Message = AgentMessages.User("before compaction")
            },
            new MessageEntry
            {
                Id = "a1",
                ParentId = "u1",
                Timestamp = DateTimeOffset.UtcNow,
                Message = new AssistantMessage([new TextContent("ok")], Usage: new UsageInfo(TotalTokens: 80), StopReason: "stop")
            },
            new CompactionEntry
            {
                Id = "c1",
                ParentId = "a1",
                Timestamp = DateTimeOffset.UtcNow,
                Summary = "summary",
                FirstKeptEntryId = "u1",
                TokensBefore = 80
            }
        };
        var footer = new FooterView();

        footer.Render(state, FooterDataProvider.CreateSnapshotFromSessionEntries(state, Environment.CurrentDirectory, entries), widthOverride: 80);

        Assert.Contains("ctx ?/100", footer.Text?.ToString());
    }

    [Fact]
    public void FooterFormattingUsesCompactTokenAbbreviations()
    {
        var usage = new UsageInfo(Input: 1500, Output: 2000, CacheRead: 1200, CacheWrite: 300, TotalTokens: 5000, Cost: new UsageCost(Total: 0.345m));
        var state = Empty() with
        {
            Transcript = [new TuiTranscriptItem("assistant", "ok", Usage: usage)],
            ContextWindow = 8000
        };
        var footer = new FooterView();

        footer.Render(state, FooterDataProvider.CreateSnapshot(state, Environment.CurrentDirectory, autoCompact: true), widthOverride: 70);
        var lines = (footer.Text?.ToString() ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        var statsLine = Assert.Single(lines, line => line.Contains("↑1.5k", StringComparison.Ordinal));
        Assert.Contains("↓2.0k", statsLine);
        Assert.Contains("(auto)", statsLine);
        Assert.True(lines.All(line => line.Length <= 70));
    }

    [Fact]
    public void FooterLinesUseParentWidthWhenOwnFrameIsUnresolved()
    {
        var parent = new View { Width = 20, Height = 4 };
        var footer = new FooterView();
        parent.Add(footer);
        var snapshot = new TuiFooterSnapshot(
            Cwd: "G:/very/long/current/working/directory",
            GitBranch: "very-long-branch-name",
            InputTokens: 1234,
            OutputTokens: 5678,
            CacheTokens: 9012,
            TotalTokens: 15924,
            ContextWindow: 20000,
            ContextPercent: 79.62,
            TotalCost: 1.234m,
            AutoCompact: true,
            ExtensionStatuses: new Dictionary<string, string> { ["ext"] = "extension status with lots of details" });

        footer.Render(Empty() with { ModelDisplay = "provider/very-long-model-name" }, snapshot);
        var text = footer.Text?.ToString() ?? string.Empty;
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.DoesNotContain("…", text);
        Assert.True(lines.Length > 3);
        Assert.All(lines, line => Assert.True(line.Length <= parent.Frame.Width, $"'{line}' exceeded {parent.Frame.Width}"));
    }

    [Fact]
    public void ChatRowRenderPipelineAppliesOverrideDecoratorsAndFinalSafety()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("extension:a", new ExtensionMessageRendererRegistration(
            "assistant-override",
            ExtensionChatRowType.Assistant,
            context =>
            [
                new ExtensionChatRow(
                    "\u001b[31mcustom " + context.Text,
                    ExtensionChatRowKind.Assistant,
                    [new ExtensionChatSpan("custom ", ExtensionChatSpanKind.Accent), new ExtensionChatSpan(context.Text)])
            ],
            ExtensionOverridePolicy.OverrideBuiltIn));
        registry.RegisterMessageDecorator("extension:a", new ExtensionMessageDecoratorRegistration(
            "badge",
            ExtensionChatRowType.Assistant,
            (_, rows) => rows.Select(row => row with { Text = "[A] " + row.Text }).ToArray(),
            Order: 10));
        registry.RegisterMessageDecorator("extension:b", new ExtensionMessageDecoratorRegistration(
            "status",
            ExtensionChatRowType.Assistant,
            (_, rows) => rows.Select(row => row with { Text = row.Text + " ok" }).ToArray(),
            Order: 20));
        var pipeline = new ChatRowRenderPipeline(registry);

        var rows = pipeline.Render(new TuiTranscriptItem("assistant", "answer"), Empty(), 24);

        var row = Assert.Single(rows);
        Assert.Equal("[A] custom answer ok", row.Text.TrimEnd());
        Assert.EndsWith("ok", row.Text.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", row.Text, StringComparison.Ordinal);
        Assert.Equal(24, row.Text.Length);
        Assert.Contains(row.Spans ?? [], span => span.Text == "custom " && span.Kind == TuiSpanKind.Accent);
    }

    [Fact]
    public void ChatRowRenderPipelinePreservesToolInteractionForOverrides()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("extension:a", new ExtensionMessageRendererRegistration(
            "tool-override",
            ExtensionChatRowType.ToolCall,
            _ => [new ExtensionChatRow("tool custom", ExtensionChatRowKind.ToolRunning)],
            ExtensionOverridePolicy.OverrideBuiltIn));
        var pipeline = new ChatRowRenderPipeline(registry);

        var rows = pipeline.Render(new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1"), Empty(), 20);

        var row = Assert.Single(rows);
        Assert.Equal("tool", row.InteractionTarget?.Kind);
        Assert.Equal("tool-1", row.InteractionTarget?.Id);
        Assert.Equal("toggle", row.InteractionTarget?.Action);
    }

    [Fact]
    public void ChatViewDelegatesTranscriptRowsToMessageRenderer()
    {
        var chat = new ChatView();
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("user", "hello")] });

        Assert.Contains(chat.Rows, row => row.Text.Contains("hello", StringComparison.Ordinal));
        Assert.Contains(chat.Rows, row => row.Text.Contains("┌", StringComparison.Ordinal));
        Assert.Contains(chat.Rows, row => row.Kind == TuiChatRowKind.User);
    }

    [Fact]
    public void ChatViewRowsNeverExceedActualFrameWidth()
    {
        var chat = new ChatView { Width = 30 };
        var state = Empty() with
        {
            Transcript = [new TuiTranscriptItem("user", "this is a narrow terminal line that must wrap")]
        };

        chat.Render(state);

        Assert.All(chat.Rows, row => Assert.True(row.Text.Length <= chat.Frame.Width, $"'{row.Text}' exceeded {chat.Frame.Width}"));
    }

    [Fact]
    public void ChatViewRowsNeverExceedTinyFrameWidth()
    {
        var chat = new ChatView { Width = 12 };
        var state = Empty() with
        {
            Transcript =
            [
                new TuiTranscriptItem("user", "tiny terminal message"),
                new TuiTranscriptItem("assistant", "assistant response"),
                new TuiTranscriptItem("tool", "tool output", ToolName: "read", ToolCallId: "tool-1")
            ]
        };

        chat.Render(state);

        Assert.DoesNotContain(chat.Rows, row => row.Text.Contains('…', StringComparison.Ordinal));
        Assert.All(chat.Rows, row => Assert.True(row.Text.Length <= chat.Frame.Width, $"'{row.Text}' exceeded {chat.Frame.Width}"));
    }

    [Fact]
    public void ChatViewRowsUseParentWidthWhenOwnFrameIsUnresolved()
    {
        var parent = new View { Width = 14, Height = 5 };
        var chat = new ChatView();
        parent.Add(chat);
        var state = Empty() with
        {
            Transcript = [new TuiTranscriptItem("assistant", "parent width should constrain rows")]
        };

        chat.Render(state);

        Assert.All(chat.Rows, row => Assert.True(row.Text.Length <= parent.Frame.Width, $"'{row.Text}' exceeded {parent.Frame.Width}"));
    }

    [Fact]
    public void ChatViewReflowsRowsWhenFrameWidthChanges()
    {
        var chat = new ChatView { Frame = new Rectangle(0, 0, 40, 5) };
        var state = Empty() with
        {
            Transcript = [new TuiTranscriptItem("assistant", "resize should rewrap these rows for the new terminal width")]
        };
        chat.Render(state);

        chat.Frame = new Rectangle(0, 0, 16, 5);

        Assert.All(chat.Rows, row => Assert.True(row.Text.Length <= 16, $"'{row.Text}' exceeded 16 after resize"));
    }

    [Fact]
    public void UserAndToolRowsAreFullWidthAndCarryKinds()
    {
        var state = Empty();
        var userRows = TuiMessageRenderer.Render(new TuiTranscriptItem("user", "hello"), state, 40);
        var toolRows = ToolExecutionView.RenderRows(new TuiTranscriptItem("tool", "details", true, "read", ToolCallId: "tool-1"), false, 40);

        Assert.All(userRows, row => Assert.Equal(40, row.Text.Length));
        Assert.All(userRows, row => Assert.Equal(TuiChatRowKind.User, row.Kind));
        Assert.All(toolRows, row => Assert.Equal(40, row.Text.Length));
        Assert.Equal(TuiChatRowKind.ToolRunning, toolRows[0].Kind);
        Assert.Equal(TuiChatRowKind.ToolRunning, toolRows[1].Kind);
    }

    [Fact]
    public void ConcreteChatRowRenderersAddVerticalPaddingInsideColoredBackgrounds()
    {
        var state = Empty();
        var userRows = TuiMessageRenderer.Render(new TuiTranscriptItem("user", "hello"), state, 40);
        var toolRows = ToolExecutionView.RenderRows(new TuiTranscriptItem("tool", "details", ToolName: "read", ToolCallId: "tool-1"), false, 40);
        var assistantRows = TuiMessageRenderer.Render(new TuiTranscriptItem("assistant", "hello"), state, 40);

        Assert.Equal(TuiChatRowKind.User, userRows[0].Kind);
        Assert.True(string.IsNullOrWhiteSpace(userRows[0].Text));
        Assert.Equal(TuiChatRowKind.User, userRows[^1].Kind);
        Assert.True(string.IsNullOrWhiteSpace(userRows[^1].Text));
        Assert.Equal(TuiChatRowKind.ToolSucceeded, toolRows[0].Kind);
        Assert.True(string.IsNullOrWhiteSpace(toolRows[0].Text));
        Assert.Equal(TuiChatRowKind.ToolSucceeded, toolRows[^1].Kind);
        Assert.True(string.IsNullOrWhiteSpace(toolRows[^1].Text));
        Assert.Equal(TuiChatRowKind.Assistant, assistantRows[0].Kind);
        Assert.True(string.IsNullOrWhiteSpace(assistantRows[0].Text));
        Assert.Equal(TuiChatRowKind.Assistant, assistantRows[^1].Kind);
        Assert.True(string.IsNullOrWhiteSpace(assistantRows[^1].Text));
    }

    [Fact]
    public void ChatViewAddsSmallPaddingAroundChatRowGroups()
    {
        var chat = new ChatView { Width = 40, Height = 10 };
        chat.Render(Empty() with { Transcript = [new TuiTranscriptItem("system", "hello")] });

        var contentRow = Assert.Single(chat.Rows, row => row.Text.Contains("[system] hello", StringComparison.Ordinal));
        Assert.StartsWith("  [system] hello", contentRow.Text, StringComparison.Ordinal);
        Assert.Equal(40, contentRow.Text.Length);
        Assert.Equal(string.Empty.PadRight(40), chat.Rows[^1].Text);
    }

    [Fact]
    public void PlannerInsertsBlankSeparatorBetweenTranscriptItems()
    {
        var planner = new ChatTranscriptRowPlanner();
        var state = Empty() with
        {
            Transcript =
            [
                new TuiTranscriptItem("user", "first"),
                new TuiTranscriptItem("assistant", "second")
            ]
        };
        var rows = planner.BuildRows(state, 40, new ChatRowCache(TuiMessageRenderer.Render));
        Assert.Contains(rows, r => string.IsNullOrWhiteSpace(r.Text));
    }

    [Fact]
    public void PlannerEmitsDimMetadataHeaderBeforeEachRoleGroup()
    {
        var planner = new ChatTranscriptRowPlanner();
        var state = Empty() with { Transcript = [new TuiTranscriptItem("user", "hello")] };
        var rows = planner.BuildRows(state, 40, new ChatRowCache(TuiMessageRenderer.Render));
        var metadata = rows.FirstOrDefault(r => r.Kind == TuiChatRowKind.System && r.Text.Contains("user", StringComparison.Ordinal));
        Assert.NotNull(metadata);
    }

    [Fact]
    public void ChatViewAutoScrollsToEndOnRender()
    {
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 12).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();

        chat.Render(Empty() with { Transcript = transcript });

        Assert.True(chat.IsAutoScroll);
        Assert.True(chat.ScrollTop > 0);
    }

    [Fact]
    public void ChatViewExposesScrollableRowCount()
    {
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 5).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();
        chat.Render(Empty() with { Transcript = transcript });

        Assert.True(chat.ScrollableRowCount > 0);
        Assert.True(chat.ScrollableRowCount >= chat.VisibleRowCount);
    }

    [Fact]
    public void ChatViewExposesVisibleRowCount()
    {
        var chat = new ChatView { Height = 3 };

        Assert.Equal(3, chat.VisibleRowCount);
    }

    [Fact]
    public void ChatViewScrollCommandsDisableAndRestoreAutoscroll()
    {
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 12).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();
        chat.Render(Empty() with { Transcript = transcript });

        chat.ScrollLine(-1);

        Assert.False(chat.IsAutoScroll);
        Assert.True(chat.ScrollTop >= 0);

        chat.ScrollToEnd();

        Assert.True(chat.IsAutoScroll);
    }

    [Fact]
    public void ChatViewScrollToSetsPositionAndDisablesAutoscroll()
    {
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 12).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();
        chat.Render(Empty() with { Transcript = transcript });

        chat.ScrollTo(4);

        Assert.Equal(4, chat.ScrollTop);
        Assert.False(chat.IsAutoScroll);
    }

    [Fact]
    public void ChatViewScrollToEndRestoresAutoscroll()
    {
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 12).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();
        chat.Render(Empty() with { Transcript = transcript });
        var maxScroll = chat.ScrollTop;

        chat.ScrollTo(2);
        Assert.False(chat.IsAutoScroll);

        chat.ScrollTo(maxScroll);

        Assert.Equal(maxScroll, chat.ScrollTop);
        Assert.True(chat.IsAutoScroll);
    }

    [Fact]
    public void ChatViewScrollToClampsToBounds()
    {
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 5).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();
        chat.Render(Empty() with { Transcript = transcript });

        chat.ScrollTo(-5);

        Assert.Equal(0, chat.ScrollTop);

        chat.ScrollTo(999);

        Assert.True(chat.ScrollTop >= 0);
        Assert.True(chat.ScrollTop <= chat.ScrollableRowCount - chat.VisibleRowCount);
    }

    [Fact]
    public void ChatViewMouseWheelScrollsTranscriptVisually()
    {
        var chat = new ChatView { Width = 80, Height = 3 };
        var transcript = Enumerable.Range(1, 12).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();
        chat.Render(Empty() with { Transcript = transcript });
        var endScrollTop = chat.ScrollTop;

        var up = SendMouse(chat, MouseFlags.WheeledUp, 0, 1);

        Assert.True(up.Handled);
        Assert.Equal(endScrollTop - 1, chat.ScrollTop);
        Assert.False(chat.IsAutoScroll);

        var down = SendMouse(chat, MouseFlags.WheeledDown, 0, 1);

        Assert.True(down.Handled);
        Assert.Equal(endScrollTop, chat.ScrollTop);
        Assert.True(chat.IsAutoScroll);
    }

    [Fact]
    public void TuiShellViewCreatesScrollBarSiblingToChat()
    {
        var shell = new TuiShellView();

        Assert.NotNull(shell.ChatScrollBar);
        Assert.Same(shell.Window, shell.ChatScrollBar.SuperView);
        Assert.Equal(1, shell.ChatScrollBar.Width);
        Assert.True(shell.ChatScrollBar.AutoShow);
        Assert.False(shell.ChatScrollBar.CanFocus);
    }

    [Fact]
    public void ShellAccentPromptBorderOnFocusAndGuttersChat()
    {
        var shell = new TuiShellView();

        shell.PromptBottomBorder.ColorScheme = TuiTheme.PromptBorderColorScheme;
        Assert.Equal(TuiTheme.PromptBorderColorScheme, shell.PromptBottomBorder.ColorScheme);

        Assert.Equal(Pos.Absolute(ChatView.ChatHorizontalGutter), shell.Chat.X);
        Assert.Equal(Pos.AnchorEnd(ChatView.ChatHorizontalGutter + 1), shell.ChatScrollBar.X);
    }

    [Fact]
    public void ScrollBarPositionSyncsFromChatViewScrollTop()
    {
        var chat = new ChatView { Height = 3 };
        var transcript = Enumerable.Range(1, 12).Select(i => new TuiTranscriptItem("system", $"line {i}")).ToArray();
        chat.Render(Empty() with { Transcript = transcript });

        var scrollBar = new ScrollBar { Width = 1, Height = 3 };
        scrollBar.ScrollableContentSize = chat.ScrollableRowCount;
        scrollBar.VisibleContentSize = chat.VisibleRowCount;
        scrollBar.Position = chat.ScrollTop;

        Assert.Equal(chat.ScrollTop, scrollBar.Position);

        chat.ScrollLine(-3);
        scrollBar.Position = chat.ScrollTop;

        Assert.Equal(chat.ScrollTop, scrollBar.Position);
        Assert.False(chat.IsAutoScroll);
    }

    [Fact]
    public void ScrollBarAutoShowsWhenContentOverflows()
    {
        var scrollBar = new ScrollBar { Width = 1, Height = 5, AutoShow = true, Visible = true };
        scrollBar.ScrollableContentSize = 20;
        scrollBar.VisibleContentSize = 5;

        Assert.True(scrollBar.Visible);

        scrollBar.ScrollableContentSize = 3;

        Assert.False(scrollBar.Visible);
    }

    [Fact]
    public void ScrollBarPositionChangedFiresOnPositionSet()
    {
        var scrollBar = new ScrollBar { Width = 1, Height = 10 };
        scrollBar.ScrollableContentSize = 50;
        scrollBar.VisibleContentSize = 10;

        int? receivedPosition = null;
        scrollBar.PositionChanged += (_, e) => receivedPosition = e.CurrentValue;

        scrollBar.Position = 15;

        Assert.Equal(15, receivedPosition);
    }

    [Fact]
    public void SelectorWindowMirrorsOriginalVisibleScrollMath()
    {
        var options = Enumerable.Range(1, 20).Select(i => $"model-{i} [provider]").ToArray();

        var rows = SelectorDialog.RenderWindow(options, 12);

        Assert.Equal(11, rows.Count);
        Assert.StartsWith("→ model-13", rows.Single(row => row.StartsWith("→", StringComparison.Ordinal)));
        Assert.Equal("  (13/20)", rows[^1]);
        Assert.DoesNotContain(rows, row => row.Contains("model-1 [provider]", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectorAcceptsArrowMarkedItemInsteadOfTerminalGuiSelection()
    {
        var options = Enumerable.Range(1, 20).Select(i => $"model-{i} [provider]").ToArray();
        var selectedIndex = 12;

        var selected = SelectorDialog.ResolveArrowSelection(options, selectedIndex);

        Assert.Equal("model-13 [provider]", selected);
    }

    [Fact]
    public void InlineSuggestionListRendersRowsWithoutWindowChrome()
    {
        var list = new InlineSuggestionListView();

        list.SetSuggestions(["/model", "/session"]);

        Assert.Equal(2, list.VisibleRowCount);
        Assert.Contains("→ /model", list.Text?.ToString());
        Assert.Contains("  /session", list.Text?.ToString());
        Assert.DoesNotContain("┌", list.Text?.ToString());
    }

    [Fact]
    public void InlineSuggestionListLinesNeverExceedFrameWidth()
    {
        var list = new InlineSuggestionListView { Width = 18 };

        list.SetSuggestions(["/model provider/very-long-model-name", "/session very-long-session-name"]);
        var text = list.Text?.ToString() ?? string.Empty;
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.DoesNotContain("…", text);
        Assert.True(lines.Length > 2);
        Assert.All(lines, line => Assert.True(line.Length <= list.Frame.Width, $"'{line}' exceeded {list.Frame.Width}"));
    }

    [Fact]
    public void InlineSuggestionListCanRenderFiveSingleLineSelectionRowsWhenOptionsAreLong()
    {
        var list = new InlineSuggestionListView { Frame = new Rectangle(0, 0, 32, 5) };
        var completions = Enumerable.Range(1, 10)
            .Select(index => new PromptCompletion($"provider/model-{index}-with-a-very-long-name", $"provider/model-{index}-with-a-very-long-name"))
            .ToArray();

        list.SetCompletions(completions, selectedIndex: 0, maxVisibleLines: 5, maxVisibleItems: 5, singleLineItems: true);
        var text = list.Text?.ToString() ?? string.Empty;
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.Equal(5, lines.Length);
        Assert.Contains("→ provider/model-1", lines[0]);
        Assert.Contains("  provider/model-5", lines[4]);
        Assert.DoesNotContain("model-6", text);
        Assert.All(lines, line => Assert.True(line.Length <= list.Frame.Width, $"'{line}' exceeded {list.Frame.Width}"));
    }

    [Fact]
    public void InlineSuggestionListKeepsFiveSingleLineSelectionRowsAfterScrolling()
    {
        var list = new InlineSuggestionListView { Frame = new Rectangle(0, 0, 32, 5) };
        var completions = Enumerable.Range(1, 10)
            .Select(index => new PromptCompletion($"provider/model-{index}-with-a-very-long-name", $"provider/model-{index}-with-a-very-long-name"))
            .ToArray();

        list.SetCompletions(completions, selectedIndex: 7, maxVisibleLines: 5, maxVisibleItems: 5, singleLineItems: true);
        var text = list.Text?.ToString() ?? string.Empty;
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        Assert.Equal(5, lines.Length);
        Assert.Contains("  provider/model-4", lines[0]);
        Assert.Contains("→ provider/model-8", lines[4]);
        Assert.DoesNotContain("model-3", text);
        Assert.DoesNotContain("model-9", text);
        Assert.All(lines, line => Assert.True(line.Length <= list.Frame.Width, $"'{line}' exceeded {list.Frame.Width}"));
    }

    [Fact]
    public void InlineSuggestionListScrollsSelectedItemIntoView()
    {
        var list = new InlineSuggestionListView { Frame = new Rectangle(0, 0, 80, 5) };
        var suggestions = Enumerable.Range(1, 10).Select(index => $"item-{index}").ToArray();

        list.SetCompletions(suggestions.Select(suggestion => PromptCompletion.FromText(suggestion)), selectedIndex: 5, maxVisibleLines: 5);

        Assert.Equal(1, list.FirstVisibleIndex);
        Assert.DoesNotContain("item-1", list.Text?.ToString());
        Assert.Contains("→ item-6", list.Text?.ToString());
    }

    [Fact]
    public void InlineSuggestionListScrollsBackUpToSelectedItem()
    {
        var list = new InlineSuggestionListView { Frame = new Rectangle(0, 0, 80, 5) };
        var suggestions = Enumerable.Range(1, 10).Select(index => $"item-{index}").ToArray();

        list.SetCompletions(suggestions.Select(suggestion => PromptCompletion.FromText(suggestion)), selectedIndex: 8, maxVisibleLines: 5);
        list.SetCompletions(suggestions.Select(suggestion => PromptCompletion.FromText(suggestion)), selectedIndex: 2, maxVisibleLines: 5);

        Assert.Equal(2, list.FirstVisibleIndex);
        Assert.Contains("→ item-3", list.Text?.ToString());
        Assert.DoesNotContain("item-1", list.Text?.ToString());
    }

    [Fact]
    public void InlineSuggestionListKeepsSelectionsPastTenVisible()
    {
        var list = new InlineSuggestionListView { Frame = new Rectangle(0, 0, 80, 5) };
        var suggestions = Enumerable.Range(1, 12).Select(index => $"item-{index}").ToArray();

        list.SetCompletions(suggestions.Select(suggestion => PromptCompletion.FromText(suggestion)), selectedIndex: 10, maxVisibleLines: 5);

        Assert.Equal(6, list.FirstVisibleIndex);
        Assert.Contains("→ item-11", list.Text?.ToString());
        Assert.DoesNotContain("item-6", list.Text?.ToString());
    }

    [Fact]
    public void PromptEditorUsesInjectedCompletionForNonSlashInput()
    {
        var prompt = new PromptEditor
        {
            CompleteText = text => text == "gpt" ? ["openai/gpt-4o"] : []
        };

        prompt.SetPromptText("gpt");

        Assert.Equal(["openai/gpt-4o"], prompt.Suggestions);
    }

    [Fact]
    public async Task PromptEditorCanSubmitEmptySelectionInput()
    {
        var submitted = "not submitted";
        var prompt = new PromptEditor { AllowEmptySubmit = true };
        prompt.Submitted += (text, _) =>
        {
            submitted = text;
            return Task.CompletedTask;
        };

        await prompt.SubmitAsync();

        Assert.Equal(string.Empty, submitted);
    }

    [Fact]
    public void InlineSelectionSessionFiltersAndResolvesSuggestions()
    {
        var session = new InlineSelectionSession("Select model", ["openai/gpt-4o — GPT-4o", "anthropic/claude-sonnet"]);

        Assert.Equal(["openai/gpt-4o — GPT-4o"], session.Complete("gpt"));
        Assert.Equal("openai/gpt-4o — GPT-4o", session.Resolve(string.Empty));
        Assert.Equal("anthropic/claude-sonnet", session.Resolve("claude"));
    }

    [Fact]
    public void PromptEditorCanAcceptSuggestionWithoutCommandSuffix()
    {
        var prompt = new PromptEditor
        {
            AcceptedSuggestionSuffix = string.Empty,
            CompleteText = _ => ["openai/gpt-4o"]
        };

        prompt.SetPromptText("gpt");
        prompt.AcceptFirstSuggestion();

        Assert.Equal("openai/gpt-4o", prompt.PromptText);
    }

    [Fact]
    public void PromptEditorCanMoveInlineSelectionWithArrowStyleNavigation()
    {
        var prompt = new PromptEditor
        {
            AllowEmptySubmit = true,
            CompleteText = _ => ["openai/gpt-4o", "openai/gpt-4.1"]
        };

        prompt.SetPromptText(string.Empty);
        prompt.MoveSuggestionSelection(1);

        Assert.Equal(1, prompt.SelectedSuggestionIndex);
        Assert.Equal("openai/gpt-4.1", prompt.SelectedSuggestion);
    }

    [Fact]
    public async Task PromptEditorSubmitUsesSelectedInlineSuggestionWhenInputIsEmpty()
    {
        var submitted = string.Empty;
        var prompt = new PromptEditor
        {
            AllowEmptySubmit = true,
            CompleteText = _ => ["openai/gpt-4o", "openai/gpt-4.1"]
        };
        prompt.Submitted += (text, _) =>
        {
            submitted = text;
            return Task.CompletedTask;
        };

        prompt.SetPromptText(string.Empty);
        prompt.MoveSuggestionSelection(1);
        await prompt.SubmitAsync();

        Assert.Equal("openai/gpt-4.1", submitted);
    }

    [Fact]
    public async Task PromptEditorSubmitUsesSelectedInlineSuggestionWhenInputIsFiltered()
    {
        var submitted = string.Empty;
        var prompt = new PromptEditor
        {
            AllowEmptySubmit = true,
            CompleteText = _ => ["openai/gpt-4o", "openai/gpt-4.1"]
        };
        prompt.Submitted += (text, _) =>
        {
            submitted = text;
            return Task.CompletedTask;
        };

        prompt.SetPromptText("gpt");
        prompt.MoveSuggestionSelection(1);
        await prompt.SubmitAsync();

        Assert.Equal("openai/gpt-4.1", submitted);
    }

    [Fact]
    public void PromptEditorRecordsAndNavigatesSubmittedPromptHistory()
    {
        var prompt = new PromptEditor();

        prompt.RecordSubmittedPrompt("first");
        prompt.RecordSubmittedPrompt("second");
        prompt.MovePromptHistory(-1);

        Assert.Equal("second", prompt.PromptText);
        prompt.MovePromptHistory(-1);
        Assert.Equal("first", prompt.PromptText);
        prompt.MovePromptHistory(1);
        Assert.Equal("second", prompt.PromptText);
    }

    [Fact]
    public void PromptEditorRestoresDraftAfterHistoryNavigation()
    {
        var prompt = new PromptEditor();
        prompt.RecordSubmittedPrompt("first");
        prompt.SetPromptText("draft");

        prompt.MovePromptHistory(-1);
        prompt.MovePromptHistory(1);

        Assert.Equal("draft", prompt.PromptText);
    }

    [Fact]
    public void PromptEditorMouseWheelRequestsTranscriptScrollInsteadOfHistoryNavigation()
    {
        var prompt = new PromptEditor();
        var deltas = new List<int>();
        prompt.TranscriptScrollRequested += deltas.Add;
        prompt.RecordSubmittedPrompt("first");
        prompt.RecordSubmittedPrompt("second");
        prompt.SetPromptText(string.Empty);

        var up = SendMouse(prompt, MouseFlags.WheeledUp, 0, 1);
        var down = SendMouse(prompt, MouseFlags.WheeledDown, 0, 1);

        Assert.True(up.Handled);
        Assert.True(down.Handled);
        Assert.Equal([-1, 1], deltas);
        Assert.Equal(string.Empty, prompt.PromptText);
    }

    [Fact]
    public void PromptEditorEmptyUpDownRequestsTranscriptScrollBeforeHistoryNavigation()
    {
        var prompt = new PromptEditor();
        var deltas = new List<int>();
        prompt.TranscriptScrollRequested += deltas.Add;
        prompt.RecordSubmittedPrompt("first");
        prompt.RecordSubmittedPrompt("second");
        prompt.SetPromptText(string.Empty);

        prompt.NewKeyDownEvent(Key.CursorUp);
        prompt.NewKeyDownEvent(Key.CursorDown);

        Assert.Equal([-1, 1], deltas);
        Assert.Equal(string.Empty, prompt.PromptText);
    }

    [Fact]
    public void PromptEditorDoesNotDuplicateConsecutiveHistoryEntries()
    {
        var prompt = new PromptEditor();

        prompt.RecordSubmittedPrompt("repeat");
        prompt.RecordSubmittedPrompt("repeat");

        Assert.Equal(["repeat"], prompt.PromptHistory);
    }

    [Fact]
    public async Task PromptEditorSubmitUsesSelectedSlashSuggestionForPartialCommand()
    {
        var submitted = string.Empty;
        var prompt = new PromptEditor
        {
            CompleteText = text => text.StartsWith("/mo", StringComparison.Ordinal) ? ["/model", "/model:1"] : []
        };
        prompt.Submitted += (text, _) =>
        {
            submitted = text;
            return Task.CompletedTask;
        };

        prompt.SetPromptText("/mo");
        prompt.MoveSuggestionSelection(1);
        await prompt.SubmitAsync();

        Assert.Equal("/model:1", submitted);
    }

    [Fact]
    public async Task PromptEditorSubmitKeepsSlashCommandWithArguments()
    {
        var submitted = string.Empty;
        var prompt = new PromptEditor
        {
            CompleteText = _ => ["/model"]
        };
        prompt.Submitted += (text, _) =>
        {
            submitted = text;
            return Task.CompletedTask;
        };

        prompt.SetPromptText("/model openai/gpt-4o");
        await prompt.SubmitAsync();

        Assert.Equal("/model openai/gpt-4o", submitted);
    }

    [Fact]
    public async Task PromptEditorSubmitKeepsExactSlashCommandWhenAnotherCompletionIsSelected()
    {
        var submitted = string.Empty;
        var prompt = new PromptEditor
        {
            CompleteText = text => string.Equals(text, "/model", StringComparison.Ordinal) ? ["/model:1", "/model"] : []
        };
        prompt.Submitted += (text, _) =>
        {
            submitted = text;
            return Task.CompletedTask;
        };

        prompt.SetPromptText("/model");
        await prompt.SubmitAsync();

        Assert.Equal("/model", submitted);
    }

    [Fact]
    public void PromptEditorAppShortcutsDoNotExecuteEditorBehavior()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("unchanged");

        var ctrlL = Record.Exception(() => prompt.NewKeyDownEvent(Key.L.WithCtrl));
        var ctrlR = Record.Exception(() => prompt.NewKeyDownEvent(Key.R.WithCtrl));
        var ctrlO = Record.Exception(() => prompt.NewKeyDownEvent(Key.O.WithCtrl));

        Assert.Null(ctrlL);
        Assert.Null(ctrlR);
        Assert.Null(ctrlO);
        Assert.Equal("unchanged", prompt.PromptText);
    }

    [Fact]
    public void PromptEditorBackspaceKeyDeletesCharacter()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("ab");
        prompt.MoveEnd();

        prompt.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("a", prompt.PromptText);
    }

    [Fact]
    public void PromptEditorLeftArrowKeyMovesCursorForInsertion()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("ab");
        prompt.MoveEnd();

        prompt.NewKeyDownEvent(Key.CursorLeft);
        prompt.InsertText("X");

        Assert.Equal("aXb", prompt.PromptText);
    }

    [Fact]
    public void PromptEditorClearPromptClearsEditorText()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("to-clear");

        prompt.ClearPrompt();

        Assert.Equal(string.Empty, prompt.PromptText);
    }

    private static MouseEventArgs SendMouse(View view, MouseFlags flags, int column, int row)
    {
        var args = new MouseEventArgs
        {
            Flags = flags,
            Position = new Point(column, row),
            ScreenPosition = new Point(column, row),
            View = view
        };
        view.NewMouseEvent(args);
        return args;
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test", ContextWindow: 100), ThinkingLevel.Off, null);

    [Fact]
    public void TsMessageRendererFallsBackToBuiltInWhenRendererThrows()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("ext1", new ExtensionMessageRendererRegistration(
            "failing-renderer",
            ExtensionChatRowType.Custom,
            Handler: context => throw new InvalidOperationException("renderer failed"),
            Override: ExtensionOverridePolicy.OverrideBuiltIn));
        var pipeline = new ChatRowRenderPipeline(registry);
        var item = new TuiTranscriptItem("custom", "test fallback content");
        var state = Empty();

        var rows = pipeline.Render(item, state, 80);

        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Text.Contains("test fallback content", StringComparison.Ordinal));
    }

    [Fact]
    public void TsMessageRendererSkipsFailedDecorator()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("ext1", new ExtensionMessageRendererRegistration(
            "good-renderer",
            ExtensionChatRowType.Custom,
            Handler: context => new[] { new ExtensionChatRow("rendered-line", ExtensionChatRowKind.Custom) },
            Override: ExtensionOverridePolicy.OverrideBuiltIn));
        registry.RegisterMessageDecorator("ext1", new ExtensionMessageDecoratorRegistration(
            "failing-decorator",
            ExtensionChatRowType.Custom,
            Handler: (context, rows) => throw new InvalidOperationException("decorator failed"),
            Order: 10));
        var pipeline = new ChatRowRenderPipeline(registry);
        var item = new TuiTranscriptItem("custom", "content");
        var state = Empty();

        var rows = pipeline.Render(item, state, 80);

        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Text.Contains("rendered-line", StringComparison.Ordinal));
    }

    [Fact]
    public void TsMessageRendererUsesCustomTypeSpecificRenderer()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("ext1", new ExtensionMessageRendererRegistration(
            "custom-card",
            ExtensionChatRowType.Custom,
            Handler: _ => [new ExtensionChatRow("card-rendered", ExtensionChatRowKind.Custom)],
            Override: ExtensionOverridePolicy.OverrideBuiltIn,
            CustomType: "custom-card"));
        registry.RegisterMessageRenderer("ext1", new ExtensionMessageRendererRegistration(
            "other-card",
            ExtensionChatRowType.Custom,
            Handler: _ => [new ExtensionChatRow("other-rendered", ExtensionChatRowKind.Custom)],
            Override: ExtensionOverridePolicy.OverrideBuiltIn,
            CustomType: "other-card"));
        var pipeline = new ChatRowRenderPipeline(registry);

        var rows = pipeline.Render(new TuiTranscriptItem("custom", "fallback", CustomType: "custom-card"), Empty(), 80);

        Assert.Contains(rows, row => row.Text.Contains("card-rendered", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Text.Contains("other-rendered", StringComparison.Ordinal));
    }

    [Fact]
    public void TsMessageRendererDoesNotUseDifferentCustomTypeRendererAsRowFallback()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("ext1", new ExtensionMessageRendererRegistration(
            "custom-card",
            ExtensionChatRowType.Custom,
            Handler: _ => [new ExtensionChatRow("card-rendered", ExtensionChatRowKind.Custom)],
            Override: ExtensionOverridePolicy.OverrideBuiltIn,
            CustomType: "custom-card"));
        var pipeline = new ChatRowRenderPipeline(registry);

        var rows = pipeline.Render(new TuiTranscriptItem("custom", "fallback-content", CustomType: "other-card"), Empty(), 80);

        Assert.DoesNotContain(rows, row => row.Text.Contains("card-rendered", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("fallback-content", StringComparison.Ordinal));
    }

    [Fact]
    public void TsMessageRendererDoesNotApplyDifferentCustomTypeDecoratorAsRowDecorator()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageDecorator("ext1", new ExtensionMessageDecoratorRegistration(
            "custom-card-decorator",
            ExtensionChatRowType.Custom,
            Handler: (_, rows) => rows.Select(row => row with { Text = "decorated:" + row.Text }).ToArray(),
            Order: 10,
            CustomType: "custom-card"));
        var pipeline = new ChatRowRenderPipeline(registry);

        var rows = pipeline.Render(new TuiTranscriptItem("custom", "fallback-content", CustomType: "other-card"), Empty(), 80);

        Assert.DoesNotContain(rows, row => row.Text.Contains("decorated:", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("fallback-content", StringComparison.Ordinal));
    }

    [Fact]
    public void TsMessageRendererFallsBackToBuiltInWhenRendererReturnsNoRows()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("ext1", new ExtensionMessageRendererRegistration(
            "empty-renderer",
            ExtensionChatRowType.Assistant,
            Handler: _ => [],
            Override: ExtensionOverridePolicy.OverrideBuiltIn));
        var pipeline = new ChatRowRenderPipeline(registry);

        var rows = pipeline.Render(new TuiTranscriptItem("assistant", "assistant fallback"), Empty(), 80);

        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Text.Contains("assistant fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInCustomRendererUsesCustomTypeAsLabel()
    {
        var pipeline = new ChatRowRenderPipeline(new ExtensionRegistry());
        var item = new TuiTranscriptItem("custom", "Approve?", CustomType: "approval-card", CustomContent: "Approve?");

        var rows = pipeline.Render(item, Empty(), 80);

        Assert.Contains(rows, row => row.Text.Contains("[approval-card]", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("Approve?", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Text == "Custom");
    }

    [Fact]
    public void BuiltInCustomRendererFallsBackToCustomLabelWhenNoCustomType()
    {
        var pipeline = new ChatRowRenderPipeline(new ExtensionRegistry());
        var item = new TuiTranscriptItem("custom", "fallback content");

        var rows = pipeline.Render(item, Empty(), 80);

        Assert.Contains(rows, row => row.Text.Contains("[custom]", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Text.Contains("fallback content", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomTypeRendererReceivesRawCustomContentAndDetails()
    {
        string? capturedContent = null;
        object? capturedDetails = null;
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("ext1", new ExtensionMessageRendererRegistration(
            "approval-card",
            ExtensionChatRowType.Custom,
            Handler: context =>
            {
                capturedContent = context.CustomContent?.ToString();
                capturedDetails = context.CustomDetails;
                return new[] { new ExtensionChatRow("rendered", ExtensionChatRowKind.Custom) };
            },
            Override: ExtensionOverridePolicy.OverrideBuiltIn,
            CustomType: "approval-card"));
        var pipeline = new ChatRowRenderPipeline(registry);

        var item = new TuiTranscriptItem(
            "custom",
            "Approve?",
            CustomType: "approval-card",
            CustomContent: "Approve?",
            CustomDetails: new { requestId = "r-1" });

        var rows = pipeline.Render(item, Empty(), 80);

        Assert.Contains(rows, row => row.Text.Contains("rendered", StringComparison.Ordinal));
        Assert.Equal("Approve?", capturedContent);
        Assert.NotNull(capturedDetails);
    }
}
