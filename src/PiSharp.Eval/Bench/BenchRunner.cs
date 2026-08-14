using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Abstractions.Messages;
using PiSharp.Eval.Events;
using PiSharp.Eval.Kernels;
using PiSharp.Extensions;

namespace PiSharp.Eval.Bench;

/// <summary>
/// Runs a bench spec: per-case runs (prompts × N) executed through the extension completion
/// surface, scored by <c>expected</c> matchers or checker kernels, with token/latency/cost
/// metrics and <c>bench_*</c> events. Serial by default (<c>eval.bench.parallelism</c>).
/// </summary>
public sealed class BenchRunner
{
    private readonly IExtensionCompletionApi _completion;
    private readonly KernelRegistry _kernels;
    private readonly BenchResultWriter _writer;
    private readonly Func<string, object?, CancellationToken, Task> _emit;
    private readonly string _cwd;
    private readonly string? _defaultProvider;
    private readonly string? _defaultModel;
    private readonly int _defaultTimeoutMs;
    private readonly Func<string> _newRunId;

    public BenchRunner(
        IExtensionCompletionApi completion,
        KernelRegistry kernels,
        BenchResultWriter writer,
        Func<string, object?, CancellationToken, Task> emit,
        string cwd,
        string? defaultProvider = null,
        string? defaultModel = null,
        int defaultTimeoutMs = EvalOptions.DefaultTimeoutMs,
        Func<string>? newRunId = null)
    {
        _completion = completion;
        _kernels = kernels;
        _writer = writer;
        _emit = emit;
        _cwd = cwd;
        _defaultProvider = defaultProvider;
        _defaultModel = defaultModel;
        _defaultTimeoutMs = defaultTimeoutMs;
        _newRunId = newRunId ?? (() => $"bench-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
    }

    public async Task<BenchRunSummary> RunAsync(BenchSpec spec, int? runsOverride = null, string? baseDir = null, CancellationToken ct = default)
    {
        var runs = runsOverride ?? spec.Runs;
        var runId = _newRunId();
        var startedAt = DateTimeOffset.UtcNow;
        var resultPath = _writer.CreateResultPath(runId);

        (var provider, var model) = ResolveModel(spec);

        await Emit(EvalEventNames.BenchRunStart,
            new BenchRunStartEvent(runId, spec.Name, runs, spec.Cases.Count, startedAt), ct);

        var results = new List<BenchCaseResult>();
        foreach (var c in spec.Cases)
        {
            for (var runIndex = 0; runIndex < runs; runIndex++)
            {
                ct.ThrowIfCancellationRequested();
                await Emit(EvalEventNames.BenchCaseStart,
                    new BenchCaseStartEvent(runId, c.Name, runIndex, c.Agent, c.Kernel), ct);

                var result = await RunCaseAsync(spec, c, runIndex, provider, model, baseDir, ct);
                results.Add(result);
                await _writer.AppendAsync(resultPath, result, ct);

                await Emit(EvalEventNames.BenchCaseEnd,
                    new BenchCaseEndEvent(runId, c.Name, runIndex, result.Passed, result.Score,
                        result.TotalTokens, result.Cost, result.LatencyMs, result.Error), ct);
            }
        }

        var summary = Aggregate(runId, spec, results, resultPath);
        await Emit(EvalEventNames.BenchRunEnd,
            new BenchRunEndEvent(runId, spec.Name, summary.Passed, summary.Cases, summary.PassRate,
                summary.TotalLatencyMs, summary.TotalTokens, summary.TotalCost, summary.ResultFile), ct);
        return summary;
    }

    public string FormatSummary(BenchRunSummary summary) => _writer.FormatSummary(summary);

    private async Task Emit(string eventName, object payload, CancellationToken ct)
    {
        try
        {
            await _emit(eventName, payload, ct);
        }
        catch (Exception)
        {
            // Event emission must never break the bench run.
        }
    }

    private (string Provider, string Model) ResolveModel(BenchSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Model))
        {
            var slash = spec.Model!.IndexOf('/');
            if (slash > 0 && slash < spec.Model.Length - 1)
                return (spec.Model[..slash], spec.Model[(slash + 1)..]);
            if (_defaultProvider is not null) return (_defaultProvider, spec.Model!);
            throw new InvalidOperationException(
                $"Bench spec '{spec.Name}' model '{spec.Model}' has no provider prefix and no default provider is configured (eval.bench.provider). Use \"provider/modelId\".");
        }
        if (_defaultProvider is not null && _defaultModel is not null)
            return (_defaultProvider, _defaultModel);
        throw new InvalidOperationException(
            $"Bench spec '{spec.Name}' declares no model and eval.bench.provider/model are not configured.");
    }

    private async Task<BenchCaseResult> RunCaseAsync(
        BenchSpec spec, BenchCase c, int runIndex, string provider, string model, string? baseDir, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var kernelExecutions = 0;
        string? output = null;
        string? error = null;
        UsageInfo? usage = null;

        try
        {
            // Optional kernel setup run once before the agent turn.
            if (!string.IsNullOrWhiteSpace(c.Kernel) && !string.IsNullOrWhiteSpace(c.Setup))
            {
                var setup = await _kernels.ExecuteAsync(c.Kernel, c.Setup!,
                    new KernelExecuteOptions(c.TimeoutMs ?? _defaultTimeoutMs), ct);
                kernelExecutions++;
                if (setup.IsError && !setup.TimedOut)
                    throw new InvalidOperationException($"case setup failed: {setup.Output}");
            }

            if (!string.IsNullOrWhiteSpace(c.Prompt))
            {
                var completion = await _completion.CompleteAsync(
                    provider, model,
                    [AgentMessages.User(c.Prompt)],
                    options: new ExtensionCompleteRequest(provider, model),
                    cancellationToken: ct);
                switch (completion.Status)
                {
                    case ExtensionCompletionStatus.Ok:
                        output = completion.Text;
                        usage = completion.Usage;
                        break;
                    case ExtensionCompletionStatus.Cancelled:
                        throw new OperationCanceledException("case run cancelled");
                    case ExtensionCompletionStatus.Timeout:
                        error = $"completion timed out";
                        break;
                    default:
                        error = completion.Error ?? "completion failed";
                        break;
                }
            }

            var (passed, score, notes) = await ScoreAsync(spec, c, output, baseDir, ct);
            sw.Stop();
            if (error is null && !passed && notes is not null) error = notes;

            return new BenchCaseResult(
                c.Name, runIndex, passed && error is null, score, error,
                usage?.Input ?? 0, usage?.Output ?? 0, usage?.TotalTokens ?? 0,
                usage?.Cost?.Total, sw.Elapsed.TotalMilliseconds, kernelExecutions, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new BenchCaseResult(
                c.Name, runIndex, false, null, ex.Message,
                usage?.Input ?? 0, usage?.Output ?? 0, usage?.TotalTokens ?? 0,
                usage?.Cost?.Total, sw.Elapsed.TotalMilliseconds, kernelExecutions, DateTimeOffset.UtcNow);
        }
    }

    private async Task<(bool Passed, double? Score, string? Notes)> ScoreAsync(
        BenchSpec spec, BenchCase c, string? output, string? baseDir, CancellationToken ct)
    {
        if (c.Checker is not null)
        {
            return await RunCheckerAsync(spec, c, output, ct);
        }

        if (c.Expected is not null)
        {
            if (c.Expected.Text is not null)
                return (string.Equals(output, c.Expected.Text, StringComparison.Ordinal), null, null);
            if (c.Expected.Regex is not null)
            {
                try
                {
                    return (Regex.IsMatch(output ?? string.Empty, c.Expected.Regex), null, null);
                }
                catch (ArgumentException ex)
                {
                    return (false, null, $"invalid expected.regex: {ex.Message}");
                }
            }
            if (c.Expected.Fixture is not null)
            {
                var fixturePath = ResolveFixture(c.Expected.Fixture, baseDir);
                try
                {
                    var fixture = await File.ReadAllTextAsync(fixturePath, ct);
                    return (string.Equals(output, fixture, StringComparison.Ordinal), null, null);
                }
                catch (Exception ex)
                {
                    return (false, null, $"fixture '{c.Expected.Fixture}' unreadable: {ex.Message}");
                }
            }
        }

        // No matcher: pass on any non-empty output.
        return (!string.IsNullOrWhiteSpace(output), null, null);
    }

    private string ResolveFixture(string fixture, string? baseDir)
    {
        if (Path.IsPathRooted(fixture)) return fixture;
        if (!string.IsNullOrWhiteSpace(baseDir)) return Path.GetFullPath(Path.Combine(baseDir, fixture));
        return Path.GetFullPath(Path.Combine(_cwd, fixture));
    }

    private async Task<(bool Passed, double? Score, string? Notes)> RunCheckerAsync(
        BenchSpec spec, BenchCase c, string? output, CancellationToken ct)
    {
        // Checkers run in a FRESH kernel so they are deterministic.
        var kernelName = c.Checker!.Kernel;
        await _kernels.ResetAsync(kernelName, ct);

        var context = new BenchCheckContext(c.Name, c.Prompt, output, c.Expected?.Text, c.Expected?.Regex);
        var result = await _kernels.ExecuteAsync(kernelName, c.Checker.Code,
            new KernelExecuteOptions(c.TimeoutMs ?? _defaultTimeoutMs), ct);
        if (result.IsError && !result.TimedOut)
            return (false, null, $"checker failed: {result.Output}");
        if (result.TimedOut)
            return (false, null, "checker timed out");

        var parsed = ParseCheckResult(result.Output);
        if (parsed is null)
            return (false, null, $"checker did not return a {{ pass, score?, notes? }} object; output: {Truncate(result.Output, 200)}");
        return (parsed.Pass, parsed.Score, parsed.Notes);
    }

    private static BenchCheckResult? ParseCheckResult(string output)
    {
        var marker = "CHECKER_RESULT:";
        var idx = output.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var json = output[(idx + marker.Length)..].Trim();
        try
        {
            return JsonSerializer.Deserialize<BenchCheckResult>(json, BenchSpecParser.SpecJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";
    private static BenchRunSummary Aggregate(string runId, BenchSpec spec, IReadOnlyList<BenchCaseResult> results, string resultPath)
    {
        var totalLatency = results.Sum(r => r.LatencyMs);
        var latencies = results.Select(r => r.LatencyMs).OrderBy(v => v).ToArray();
        double? median = latencies.Length == 0 ? null
            : latencies.Length % 2 == 1
                ? latencies[latencies.Length / 2]
                : (latencies[latencies.Length / 2 - 1] + latencies[latencies.Length / 2]) / 2.0;
        var totalCost = results.Select(r => r.Cost).Sum();
        return new BenchRunSummary(
            runId, spec.Name, results.Count, results.Count(r => r.Passed),
            results.Count == 0 ? 0 : (double)results.Count(r => r.Passed) / results.Count,
            totalLatency,
            results.Count == 0 ? null : totalLatency / results.Count,
            median,
            results.Sum(r => r.TotalTokens),
            totalCost,
            resultPath);
    }

}
