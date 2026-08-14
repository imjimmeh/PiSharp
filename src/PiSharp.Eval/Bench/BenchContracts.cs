namespace PiSharp.Eval.Bench;

/// <summary>
/// A bench spec: a repeatable scored run. Plain JSON files under <c>eval.benchDir</c>
/// (default <c>&lt;cwd&gt;/.pi/bench/</c>), so suites are portable, diffable, and shippable.
/// </summary>
public sealed record BenchSpec
{
    public string Name { get; init; } = string.Empty;
    public string? Model { get; init; }                     // "provider/modelId"; defaults to eval.bench.provider/model
    public int Runs { get; init; } = 1;
    public IReadOnlyList<BenchCase> Cases { get; init; } = [];
}

public sealed record BenchCase(
    string Name,
    string Prompt,
    string? Kernel = null,                     // "csharp" | "python" | null = no kernel
    string? Setup = null,                      // kernel code run once before the agent turn
    BenchExpected? Expected = null,            // text/regex/fixture shorthand
    BenchChecker? Checker = null,              // kernel script scoring transcript/artifacts
    string? Agent = null,                      // P06 agent name → typed task spawn
    int? TimeoutMs = null);

public sealed record BenchExpected(string? Text = null, string? Regex = null, string? Fixture = null);
public sealed record BenchChecker(string Kernel, string Code);   // returns { pass, score?, notes? }

/// <summary>
/// Context handed to a checker kernel script. The script returns
/// <c>{ pass, score?, notes? }</c> (see <see cref="BenchCheckResult"/>).
/// </summary>
public sealed record BenchCheckContext(
    string CaseName,
    string Prompt,
    string? Output,
    string? ExpectedText,
    string? ExpectedRegex);

public sealed record BenchCheckResult(bool Pass, double? Score = null, string? Notes = null);

public sealed record BenchCaseResult(
    string CaseName,
    int RunIndex,
    bool Passed,
    double? Score,
    string? Error,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    decimal? Cost,
    double LatencyMs,
    int KernelExecutions,
    DateTimeOffset FinishedAt);

public sealed record BenchRunSummary(
    string RunId,
    string SpecName,
    int Cases,
    int Passed,
    double PassRate,
    double TotalLatencyMs,
    double? MeanLatencyMs,
    double? MedianLatencyMs,
    int TotalTokens,
    decimal? TotalCost,
    string? ResultFile);
