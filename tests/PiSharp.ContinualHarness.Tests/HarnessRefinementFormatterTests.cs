using PiSharp.ContinualHarness;
using Xunit;

namespace PiSharp.ContinualHarness.Tests;

public sealed class HarnessRefinementFormatterTests
{
    [Fact]
    public void Diff_Lines_Are_Marked()
    {
        var diff = HarnessRefinementFormatter.Diff("a\nb\nc", "a\nX\nc");
        var lines = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.StartsWith("-b"));
        Assert.Contains(lines, l => l.StartsWith("+X"));
        Assert.Contains(lines, l => l == " a" || l.StartsWith(" a"));
    }

    [Fact]
    public void Diff_Is_Bounded()
    {
        var hugeOld = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"old{i}"));
        var hugeNew = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"new{i}"));
        var diff = HarnessRefinementFormatter.Diff(hugeOld, hugeNew);
        var lineCount = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(lineCount <= HarnessRefinementFormatter.DiffLineLimit + 1, $"got {lineCount} lines");
        Assert.Contains("truncated", diff);
    }

    [Fact]
    public void BoundExcerpt_Caps_Length()
    {
        var longText = new string('x', 1000);
        var excerpt = HarnessRefinementFormatter.BoundExcerpt(longText, 100);
        Assert.Equal(101, excerpt.Length); // 100 chars + ellipsis
    }

    [Fact]
    public void FormatEntry_Marks_Dirty()
    {
        var entry = new PiSharp.ContinualHarness.Contracts.HarnessEntry
        {
            Key = new PiSharp.ContinualHarness.Contracts.HarnessEntryKey(PiSharp.ContinualHarness.Contracts.HarnessRefinementKind.Prompt, "x"),
            Version = 2,
            Content = System.Text.Json.JsonSerializer.SerializeToElement(new { markdown = "x" }),
            Scope = PiSharp.ContinualHarness.Contracts.HarnessRefinementScope.Local,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastRefinementId = 2,
            Dirty = true,
        };
        var text = HarnessRefinementFormatter.FormatEntry(entry);
        Assert.Contains("[dirty]", text);
        Assert.Contains("prompt/x", text);
    }
}

public sealed class P08MemoryStoreAdapterTests
{
    [Fact]
    public void Deserialize_Serialize_RoundTrip()
    {
        var content = HarnessTestJson.Memory("The title", "The body", kind: "lesson");
        var record = P08MemoryStoreAdapter.Deserialize("refine/lesson", content);
        Assert.Equal("The title", record.Title);
        Assert.Equal("The body", record.Content);

        var back = P08MemoryStoreAdapter.Serialize(record);
        Assert.Equal("The title", back.GetProperty("title").GetString());
        Assert.Equal("The body", back.GetProperty("content").GetString());
        Assert.Equal("lesson", back.GetProperty("kind").GetString());
        Assert.Equal("refine/lesson", back.GetProperty("recordKey").GetString());
    }
}
