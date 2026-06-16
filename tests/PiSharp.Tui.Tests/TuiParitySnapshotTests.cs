using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using System.Text.RegularExpressions;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiParitySnapshotTests
{
    [Fact]
    public void SnapshotAssistantThinkingAndToolLifecycle()
    {
        var state = Empty();
        var assistant = new TuiTranscriptItem("assistant", string.Empty, Content:
        [
            new TextContent("### Plan\n- first\n- second"),
            new ThinkingContent("hidden chain")
        ]);
        var runningTool = new TuiTranscriptItem("tool", "stream output", true, "read");
        var completeTool = new TuiTranscriptItem("tool", "done", false, "read");

        var rows = new List<string>();
        rows.AddRange(TuiMessageRenderer.Render(assistant, state, 48).Select(row => row.Text.TrimEnd()));
        rows.Add("--");
        rows.AddRange(ToolExecutionView.Render(runningTool, true, 48));
        rows.AddRange(ToolExecutionView.Render(completeTool, false, 48));

        AssertMatchesSnapshot("assistant-tool-lifecycle.snap", string.Join(Environment.NewLine, rows));
    }

    [Fact]
    public void SnapshotFooterNarrowWidthLayout()
    {
        var usage = new UsageInfo(Input: 2200, Output: 3500, CacheRead: 800, CacheWrite: 100, TotalTokens: 6600, Cost: new UsageCost(Total: 1.234m));
        var state = Empty() with
        {
            Transcript = [new TuiTranscriptItem("assistant", "ok", Usage: usage)],
            ContextWindow = 8000,
            ExtensionStatuses = new Dictionary<string, string>
            {
                ["ext.z"] = "ready",
                ["ext.a"] = "syncing"
            }
        };

        var footer = new FooterView();

        var snapshot = new TuiFooterSnapshot(
            Cwd: "PiSharp",
            GitBranch: null,
            InputTokens: 2200,
            OutputTokens: 3500,
            CacheTokens: 900,
            TotalTokens: 6600,
            TotalCost: 1.234m,
            ContextPercent: 82.5,
            ContextWindow: 8000,
            AutoCompact: true,
            ExtensionStatuses: new Dictionary<string, string>
            {
                ["ext.z"] = "ready",
                ["ext.a"] = "syncing"
            });

        footer.Render(state, snapshot, widthOverride: 50);
        AssertMatchesSnapshot("footer-narrow.snap", footer.Text?.ToString() ?? string.Empty);
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("openai", "gpt-4o", "openai", Name: "GPT-4o", ContextWindow: 8000), ThinkingLevel.Low, "session");

    private static void AssertMatchesSnapshot(string fileName, string actual)
    {
        var root = AppContext.BaseDirectory;
        var snapshots = Path.Combine(root, "Snapshots");
        var path = Path.Combine(snapshots, fileName);
        var expected = Normalize(File.ReadAllText(path));
        Assert.Equal(expected, Normalize(actual));
    }

    private static string Normalize(string text)
        => string.Join(
            Environment.NewLine,
            (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => NormalizeLine(line.TrimEnd())))
            .TrimEnd();

    private static string NormalizeLine(string line)
    {
        var collapsedDashes = Regex.Replace(line, "─{3,}", "───");
        return Regex.Replace(collapsedDashes, "\\s+│$", " │");
    }
}
