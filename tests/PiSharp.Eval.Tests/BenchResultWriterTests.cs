using System.Text.Json;
using PiSharp.Eval.Bench;
using Xunit;

namespace PiSharp.Eval.Tests;

/// <summary>
/// Bench result writer tests: JSONL shape (one <see cref="BenchCaseResult"/> per line,
/// snake_case), append semantics, and summary formatting.
/// </summary>
public sealed class BenchResultWriterTests
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static BenchCaseResult SampleResult(string caseName = "c1", int runIndex = 0) => new(
        CaseName: caseName,
        RunIndex: runIndex,
        Passed: true,
        Score: 0.75,
        Error: null,
        InputTokens: 10,
        OutputTokens: 20,
        TotalTokens: 30,
        Cost: 0.0012m,
        LatencyMs: 123.4,
        KernelExecutions: 2,
        FinishedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task AppendAsync_WritesJsonlLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"eval-bench-{Guid.NewGuid():N}");
        try
        {
            var writer = new BenchResultWriter(dir);
            var path = writer.CreateResultPath("run-1");
            Assert.EndsWith(".jsonl", path);

            await writer.AppendAsync(path, SampleResult());
            await writer.AppendAsync(path, SampleResult("c2", 1));

            var lines = (await File.ReadAllLinesAsync(path)).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, lines.Length);

            var parsed = JsonSerializer.Deserialize<BenchCaseResult>(lines[0], SnakeCaseOptions);
            Assert.NotNull(parsed);
            Assert.Equal("c1", parsed.CaseName);
            Assert.True(parsed.Passed);
            Assert.Equal(0.75, parsed.Score);
            Assert.Equal(30, parsed.TotalTokens);
            Assert.Equal(123.4, parsed.LatencyMs);
            Assert.Equal(2, parsed.KernelExecutions);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateResultPath_SanitizesRunId()
    {
        var writer = new BenchResultWriter(Path.GetTempPath());
        var path = writer.CreateResultPath("bench/..\\weird:name");
        Assert.DoesNotContain("/", Path.GetFileName(path));
        Assert.DoesNotContain(":", Path.GetFileName(path));
    }

    [Fact]
    public void FormatSummary_ContainsPassRateAndTokens()
    {
        var writer = new BenchResultWriter(Path.GetTempPath());
        var summary = new BenchRunSummary(
            RunId: "run-1",
            SpecName: "smoke",
            Cases: 4,
            Passed: 3,
            PassRate: 0.75,
            TotalLatencyMs: 400,
            MeanLatencyMs: 100,
            MedianLatencyMs: 90,
            TotalTokens: 120,
            TotalCost: 0.01m,
            ResultFile: @"C:\runs\run-1.jsonl");

        var text = writer.FormatSummary(summary);

        Assert.Contains("smoke", text);
        Assert.Contains("3/4 passed", text);
        Assert.Contains("120", text);
        Assert.Contains("run-1.jsonl", text);
    }
}
