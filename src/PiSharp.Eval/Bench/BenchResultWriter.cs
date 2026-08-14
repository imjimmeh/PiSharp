using System.Text;
using System.Text.Json;

namespace PiSharp.Eval.Bench;

/// <summary>
/// Writes bench results: one <see cref="BenchCaseResult"/> JSONL line per case run under
/// <c>&lt;benchDir&gt;/runs/&lt;runId&gt;.jsonl</c> (snake_case, the <c>--mode json</c>
/// convention), plus the human-readable summary text for the slash-command result.
/// </summary>
public sealed class BenchResultWriter(string benchDir)
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public string RunsDirectory => Path.Combine(benchDir, "runs");

    public string CreateResultPath(string runId)
        => Path.Combine(RunsDirectory, Sanitize(runId) + ".jsonl");

    public async Task AppendAsync(string path, BenchCaseResult result, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(result, ResultJsonOptions);
        await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);
    }

    public string FormatSummary(BenchRunSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bench '{summary.SpecName}' ({summary.RunId}): {summary.Passed}/{summary.Cases} passed ({summary.PassRate:P0})");
        if (summary.MeanLatencyMs is not null) sb.AppendLine($"  mean latency: {summary.MeanLatencyMs.Value:F0} ms");
        if (summary.MedianLatencyMs is not null) sb.AppendLine($"  median latency: {summary.MedianLatencyMs.Value:F0} ms");
        sb.AppendLine($"  total tokens: {summary.TotalTokens}  total cost: {summary.TotalCost?.ToString("C") ?? "n/a"}");
        if (!string.IsNullOrEmpty(summary.ResultFile)) sb.AppendLine($"  results: {summary.ResultFile}");
        return sb.ToString().TrimEnd();
    }

    private static string Sanitize(string value)
        => string.Concat((value ?? "run").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
}
