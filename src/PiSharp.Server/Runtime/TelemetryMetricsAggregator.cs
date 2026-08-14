using PiSharp.Extensions;
using PiSharp.Runtime.Telemetry;
using PiSharp.Server.Contracts;

namespace PiSharp.Server.Runtime;

/// <summary>
/// Daemon-side telemetry aggregator (an <see cref="ITelemetrySink"/> fed by the per-session
/// <see cref="TelemetryService"/> instances the host wires into every runtime). Consumes the
/// canonical records the core instrumentor emits (§4.3 of the P25 plan) and projects them into
/// the pull-based <see cref="MetricsSnapshot"/> served by <c>get_metrics</c>: turn/token counts
/// and latencies come from <c>turn_ended</c> events, tool/compaction/session counters from the
/// matching metric records. Read-only and lock-guarded; safe for concurrent sessions.
/// </summary>
public sealed class TelemetryMetricsAggregator : ITelemetrySink
{
    private readonly object _gate = new();
    private readonly bool _enabled;

    private long _sessionCount;
    private long _turnCount;
    private long _tokensIn;
    private long _tokensOut;
    private long _tokensCache;
    private readonly List<double> _latencyMs = [];
    private long _toolCalls;
    private long _toolFailures;
    private long _toolRetries;
    private long _compactions;

    private readonly Dictionary<string, ModelAccumulator> _byModel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolAccumulator> _byTool = new(StringComparer.Ordinal);

    public TelemetryMetricsAggregator(bool enabled = true)
    {
        _enabled = enabled;
    }

    /// <summary>True when the host wired telemetry into its runtimes; disabled aggregators always report <see cref="MetricsSnapshot.Disabled"/>.</summary>
    public bool Enabled => _enabled;

    public string Name => "server-metrics-aggregator";

    public void RecordMetric(TelemetryMetricRecord metric)
    {
        switch (metric.Name)
        {
            case TelemetryInstrumentNames.ToolCalls:
                AddTool(metric, (acc, v) => acc.Calls += v, global: v => _toolCalls += v);
                break;
            case TelemetryInstrumentNames.ToolFailures:
                AddTool(metric, (acc, v) => acc.Failures += v, global: v => _toolFailures += v);
                break;
            case TelemetryInstrumentNames.ToolRetries:
                AddTool(metric, (acc, v) => acc.Retries += v, global: v => _toolRetries += v);
                break;
            case TelemetryInstrumentNames.ToolDuration:
                AddToolDuration(metric);
                break;
            case TelemetryInstrumentNames.Compactions:
                lock (_gate) _compactions += (long)metric.Value;
                break;
            case TelemetryInstrumentNames.SessionActive:
                lock (_gate) _sessionCount = Math.Max(0, _sessionCount + (long)metric.Value);
                break;
        }
    }

    public void RecordEvent(TelemetryEventRecord evt)
    {
        if (!string.Equals(evt.Name, TelemetryEventNames.TurnEnded, StringComparison.Ordinal)) return;

        var attributes = evt.Attributes;
        var latencyMs = ReadDouble(attributes, "latencyMs");
        var tokensIn = ReadLong(attributes, "tokensIn");
        var tokensOut = ReadLong(attributes, "tokensOut");
        var tokensCache = ReadLong(attributes, "tokensCache");
        var model = ReadString(attributes, "model") ?? "unknown";

        lock (_gate)
        {
            _turnCount++;
            _tokensIn += tokensIn;
            _tokensOut += tokensOut;
            _tokensCache += tokensCache;
            _latencyMs.Add(latencyMs);

            if (!_byModel.TryGetValue(model, out var acc))
            {
                acc = new ModelAccumulator();
                _byModel[model] = acc;
            }

            acc.Turns++;
            acc.TokensIn += tokensIn;
            acc.TokensOut += tokensOut;
            acc.TokensCache += tokensCache;
            acc.LatencyMsTotal += latencyMs;
        }
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
    }

    public MetricsSnapshot BuildSnapshot(DateTimeOffset generatedAt, IReadOnlyList<string>? recentJournalLines = null)
    {
        if (!_enabled) return MetricsSnapshot.Disabled(generatedAt);

        lock (_gate)
        {
            var (avgLatencyMs, p95LatencyMs) = ComputeLatencyPercentiles(_latencyMs);
            var byModel = _byModel
                .Select(pair => new PerModelMetrics(
                    pair.Key,
                    pair.Value.Turns,
                    pair.Value.TokensIn,
                    pair.Value.TokensOut,
                    pair.Value.TokensCache,
                    pair.Value.Turns == 0 ? 0 : pair.Value.LatencyMsTotal / pair.Value.Turns))
                .OrderByDescending(m => m.Turns)
                .ToArray();
            var byTool = _byTool
                .Select(pair => new PerToolMetrics(
                    pair.Key,
                    pair.Value.Calls,
                    pair.Value.Failures,
                    pair.Value.Retries,
                    pair.Value.DurationCount == 0 ? 0 : pair.Value.DurationMsTotal / pair.Value.DurationCount))
                .OrderByDescending(t => t.Calls)
                .ToArray();

            return new MetricsSnapshot(
                Enabled: true,
                generatedAt,
                Math.Max(0, _sessionCount),
                _turnCount,
                _tokensIn,
                _tokensOut,
                _tokensCache,
                _toolCalls,
                _toolFailures,
                _toolRetries,
                _compactions,
                avgLatencyMs,
                p95LatencyMs,
                byModel,
                byTool,
                recentJournalLines ?? []);
        }
    }

    private void AddTool(TelemetryMetricRecord metric, Action<ToolAccumulator, long> apply, Action<long> global)
    {
        var tool = ReadString(metric.Attributes, "tool") ?? "unknown";
        var value = (long)metric.Value;
        lock (_gate)
        {
            if (!_byTool.TryGetValue(tool, out var acc))
            {
                acc = new ToolAccumulator();
                _byTool[tool] = acc;
            }

            apply(acc, value);
            global(value);
        }
    }

    private void AddToolDuration(TelemetryMetricRecord metric)
    {
        var tool = ReadString(metric.Attributes, "tool") ?? "unknown";
        lock (_gate)
        {
            if (!_byTool.TryGetValue(tool, out var acc))
            {
                acc = new ToolAccumulator();
                _byTool[tool] = acc;
            }

            acc.DurationMsTotal += metric.Value * 1000.0;
            acc.DurationCount++;
        }
    }

    private static (double AverageMs, double P95Ms) ComputeLatencyPercentiles(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 0);
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var average = sorted.Average();
        var index = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
        return (average, sorted[Math.Clamp(index, 0, sorted.Length - 1)]);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> attributes, string key)
        => attributes.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static long ReadLong(IReadOnlyDictionary<string, object?> attributes, string key)
        => attributes.TryGetValue(key, out var value) ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static double ReadDouble(IReadOnlyDictionary<string, object?> attributes, string key)
        => attributes.TryGetValue(key, out var value) ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private sealed class ModelAccumulator
    {
        public long Turns;
        public long TokensIn;
        public long TokensOut;
        public long TokensCache;
        public double LatencyMsTotal;
    }

    private sealed class ToolAccumulator
    {
        public long Calls;
        public long Failures;
        public long Retries;
        public double DurationMsTotal;
        public long DurationCount;
    }
}
