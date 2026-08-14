using System.Text.Json;

using System.Text;
using PiSharp.ContinualHarness.Contracts;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Human-readable rendering of records/entries and a bounded line diff for conflict display.
/// All outputs are bounded so hostile content cannot flood the UI.
/// </summary>
public static class HarnessRefinementFormatter
{
    public const int DiffLineLimit = 200;

    public static string FormatEntry(HarnessEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append($"{entry.Key}  v{entry.Version}  scope={entry.Scope.ToString().ToLowerInvariant()}");
        if (entry.Dirty) sb.Append("  [dirty]");
        sb.Append($"  updated={entry.UpdatedAt:O}");
        sb.Append($"  #ref={entry.LastRefinementId}");
        return sb.ToString();
    }

    public static string FormatRecord(HarnessRefinementRecord record)
    {
        var sb = new StringBuilder();
        sb.Append($"#{record.RefinementId} {record.Action.ToString().ToLowerInvariant()} {record.Key} v{record.Version}");
        sb.Append($" by {record.Author} [{record.Scope.ToString().ToLowerInvariant()}]");
        if (record.TargetVersion is { } target) sb.Append($" ->v{target}");
        if (record.Deleted) sb.Append(" (deleted)");
        if (!string.IsNullOrWhiteSpace(record.Reason)) sb.Append($" ({record.Reason})");
        return sb.ToString();
    }

    public static string RenderContent(JsonElement content)
        => content.ValueKind == JsonValueKind.Object && content.TryGetProperty("markdown", out var markdown)
            ? markdown.GetString() ?? string.Empty
            : content.GetRawText();

    /// <summary>
    /// Renders a bounded unified-style line diff between two texts. Output is capped at
    /// <see cref="DiffLineLimit"/> lines to bound hostile input.
    /// </summary>
    public static string Diff(string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');
        var ops = ComputeDiff(oldLines, newLines);

        var sb = new StringBuilder();
        var emitted = 0;
        foreach (var (tag, text) in ops)
        {
            if (emitted >= DiffLineLimit)
            {
                sb.AppendLine("…(diff truncated)");
                break;
            }
            sb.Append(tag).Append(text);
            sb.Append('\n');
            emitted++;
        }
        return sb.ToString();
    }

    /// <summary>A tolerant equality used by the diff so trailing differences on a line still show.</summary>
    private static bool LinesEqual(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Standard LCS-based diff returning (" "/"-"/"+", line) operations.</summary>
    internal static IReadOnlyList<(char Tag, string Text)> ComputeDiff(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length, m = newLines.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = LinesEqual(oldLines[i], newLines[j])
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (LinesEqual(oldLines[x], newLines[y]))
            {
                result.Add((' ', oldLines[x]));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(('-', oldLines[x]));
                x++;
            }
            else
            {
                result.Add(('+', newLines[y]));
                y++;
            }
        }
        while (x < n) { result.Add(('-', oldLines[x])); x++; }
        while (y < m) { result.Add(('+', newLines[y])); y++; }
        return result;
    }

    /// <summary>Bounded free-text excerpt (default max 500 chars).</summary>
    public static string BoundExcerpt(string? excerpt, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(excerpt)) return string.Empty;
        if (excerpt.Length <= maxChars) return excerpt;
        return excerpt[..maxChars] + "…";
    }
}
