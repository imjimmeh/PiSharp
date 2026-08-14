using PiSharp.Extensions;
using PiSharp.Runtime.Telemetry;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using Xunit;

namespace PiSharp.Server.Observability.Tests;

/// <summary>
/// Unit coverage for <see cref="TelemetryMetricsAggregator"/> (P25 C6): the daemon-side sink that
/// projects canonical core-instrumentor records into the pull-based <see cref="MetricsSnapshot"/>.
/// Verifies the turn/token accumulation, per-model and per-tool breakdowns, tool-duration
/// seconds→ms conversion, latency percentiles, gating by <c>enabled</c>, and journal passthrough.
/// </summary>
public sealed class TelemetryMetricsAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyAggregator_ProducesZeroedEnabledSnapshot()
    {
        var aggregator = new TelemetryMetricsAggregator(enabled: true);

        var snapshot = aggregator.BuildSnapshot(Now);

        Assert.True(snapshot.Enabled);
        Assert.Equal(Now, snapshot.GeneratedAt);
        Assert.Equal(0, snapshot.SessionCount);
        Assert.Equal(0, snapshot.TurnCount);
        Assert.Equal(0, snapshot.TokenCountIn);
        Assert.Equal(0, snapshot.TokenCountOut);
        Assert.Equal(0, snapshot.TokenCountCache);
        Assert.Equal(0, snapshot.ToolCallCount);
        Assert.Equal(0, snapshot.ToolFailureCount);
        Assert.Equal(0, snapshot.ToolRetryCount);
        Assert.Equal(0, snapshot.CompactionCount);
        Assert.Equal(0, snapshot.TurnLatencyAvgMs);
        Assert.Equal(0, snapshot.TurnLatencyP95Ms);
        Assert.Empty(snapshot.ByModel);
        Assert.Empty(snapshot.ByTool);
        Assert.Empty(snapshot.RecentJournalLines);
    }

    [Fact]
    public void DisabledAggregator_ProducesCanonicalDisabledSnapshot()
    {
        var aggregator = new TelemetryMetricsAggregator(enabled: false);

        var snapshot = aggregator.BuildSnapshot(Now);

        Assert.False(snapshot.Enabled);
        Assert.Equal(0, snapshot.TurnCount);
        Assert.Equal(0, snapshot.TokenCountIn);
        Assert.Empty(snapshot.ByModel);
        Assert.Empty(snapshot.ByTool);
    }

    [Fact]
    public void TurnEnded_AccumulatesCountsAndPerModelBreakdown()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordEvent(TurnEnded("model-a", latencyMs: 1000, tokensIn: 100, tokensOut: 20, tokensCache: 5));
        aggregator.RecordEvent(TurnEnded("model-a", latencyMs: 3000, tokensIn: 200, tokensOut: 40, tokensCache: 10));
        aggregator.RecordEvent(TurnEnded("model-b", latencyMs: 2000, tokensIn: 50, tokensOut: 10, tokensCache: 0));

        var snapshot = aggregator.BuildSnapshot(Now);

        Assert.Equal(3, snapshot.TurnCount);
        Assert.Equal(350, snapshot.TokenCountIn);
        Assert.Equal(70, snapshot.TokenCountOut);
        Assert.Equal(15, snapshot.TokenCountCache);
        // Global average over all recorded latencies.
        Assert.Equal(2000, snapshot.TurnLatencyAvgMs);

        // model-a accumulated 2 turns; model-b 1 turn → model-a sorts first.
        Assert.Equal(2, snapshot.ByModel.Count);
        var modelA = snapshot.ByModel[0];
        Assert.Equal("model-a", modelA.Model);
        Assert.Equal(2, modelA.Turns);
        Assert.Equal(300, modelA.TokensIn);
        Assert.Equal(60, modelA.TokensOut);
        Assert.Equal(15, modelA.TokensCache);
        Assert.Equal(2000, modelA.AvgLatencyMs); // (1000 + 3000) / 2

        var modelB = snapshot.ByModel[1];
        Assert.Equal("model-b", modelB.Model);
        Assert.Equal(1, modelB.Turns);
        Assert.Equal(50, modelB.TokensIn);
        Assert.Equal(2000, modelB.AvgLatencyMs);
    }

    [Fact]
    public void ToolMetrics_AccumulateGloballyAndPerTool()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolCalls, 1, "tool", "bash"));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolCalls, 1, "tool", "bash"));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolCalls, 3, "tool", "grep"));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolFailures, 1, "tool", "bash"));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolRetries, 2, "tool", "bash"));

        var snapshot = aggregator.BuildSnapshot(Now);

        Assert.Equal(5, snapshot.ToolCallCount);
        Assert.Equal(1, snapshot.ToolFailureCount);
        Assert.Equal(2, snapshot.ToolRetryCount);

        // Breakdown is ordered by call count descending → grep (3) before bash (2).
        Assert.Equal(2, snapshot.ByTool.Count);
        var grep = snapshot.ByTool[0];
        Assert.Equal("grep", grep.Tool);
        Assert.Equal(3, grep.Calls);
        Assert.Equal(0, grep.Failures);
        Assert.Equal(0, grep.Retries);

        var bash = snapshot.ByTool[1];
        Assert.Equal("bash", bash.Tool);
        Assert.Equal(2, bash.Calls);
        Assert.Equal(1, bash.Failures);
        Assert.Equal(2, bash.Retries);
    }

    [Fact]
    public void ToolDuration_ConvertsSecondsToMillisecondsPerTool()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolDuration, 0.5, "tool", "bash"));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolDuration, 1.5, "tool", "bash"));

        var snapshot = aggregator.BuildSnapshot(Now);

        var bash = snapshot.ByTool.Single();
        Assert.Equal(0, bash.Calls); // durations don't count as calls
        Assert.Equal(1000, bash.AvgDurationMs); // (0.5 * 1000 + 1.5 * 1000) / 2
    }

    [Fact]
    public void CompactionAndSessionActive_UpdateGlobalCounters()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.Compactions, 1));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.Compactions, 1));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.SessionActive, 1));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.SessionActive, 1));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.SessionActive, -1));

        var snapshot = aggregator.BuildSnapshot(Now);

        Assert.Equal(2, snapshot.CompactionCount);
        Assert.Equal(1, snapshot.SessionCount);
    }

    [Fact]
    public void SessionActive_NeverGoesNegative()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.SessionActive, -5));

        var snapshot = aggregator.BuildSnapshot(Now);
        Assert.Equal(0, snapshot.SessionCount);
    }

    [Fact]
    public void LatencyPercentiles_ComputesAverageAndP95()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordEvent(TurnEnded("m", latencyMs: 1000));
        aggregator.RecordEvent(TurnEnded("m", latencyMs: 2000));
        aggregator.RecordEvent(TurnEnded("m", latencyMs: 3000));

        var snapshot = aggregator.BuildSnapshot(Now);

        Assert.Equal(2000, snapshot.TurnLatencyAvgMs);
        Assert.Equal(3000, snapshot.TurnLatencyP95Ms); // p95 of 3 values falls on the max
    }

    [Fact]
    public void MissingAttributes_DefaultToZeroOrUnknownBucket()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordEvent(new TelemetryEventRecord(TelemetryEventNames.TurnEnded, Now, new Dictionary<string, object?>()));
        aggregator.RecordMetric(Instrument(TelemetryInstrumentNames.ToolCalls, 2)); // no tool attribute

        var snapshot = aggregator.BuildSnapshot(Now);

        Assert.Equal(1, snapshot.TurnCount);
        Assert.Equal(0, snapshot.TokenCountIn);
        Assert.Equal(0, snapshot.TurnLatencyAvgMs);
        var model = Assert.Single(snapshot.ByModel);
        Assert.Equal("unknown", model.Model);
        var tool = Assert.Single(snapshot.ByTool);
        Assert.Equal("unknown", tool.Tool);
        Assert.Equal(2, tool.Calls);
    }

    [Fact]
    public void RecentJournalLines_ArePassedThrough()
    {
        var aggregator = new TelemetryMetricsAggregator();
        IReadOnlyList<string> journal = ["line-a", "line-b"];

        var snapshot = aggregator.BuildSnapshot(Now, journal);

        Assert.Equal(["line-a", "line-b"], snapshot.RecentJournalLines);
    }

    [Fact]
    public void NonTurnEndedEvents_AreIgnored()
    {
        var aggregator = new TelemetryMetricsAggregator();

        aggregator.RecordEvent(new TelemetryEventRecord(TelemetryEventNames.DaemonStarted, Now, new Dictionary<string, object?>()));
        aggregator.RecordEvent(new TelemetryEventRecord("some_other_event", Now, new Dictionary<string, object?>()));

        var snapshot = aggregator.BuildSnapshot(Now);
        Assert.Equal(0, snapshot.TurnCount);
        Assert.Empty(snapshot.ByModel);
    }

    private static TelemetryEventRecord TurnEnded(string model, double latencyMs, long tokensIn = 0, long tokensOut = 0, long tokensCache = 0)
        => new(
            TelemetryEventNames.TurnEnded,
            Now,
            new Dictionary<string, object?>
            {
                ["latencyMs"] = latencyMs,
                ["tokensIn"] = tokensIn,
                ["tokensOut"] = tokensOut,
                ["tokensCache"] = tokensCache,
                ["model"] = model
            });

    private static TelemetryMetricRecord Instrument(string name, double value, params object[] attributePairs)
    {
        var attributes = new Dictionary<string, object?>();
        for (var i = 0; i < attributePairs.Length; i += 2)
            attributes[(string)attributePairs[i]] = attributePairs[i + 1];
        return new TelemetryMetricRecord(name, value, Now, attributes);
    }
}
