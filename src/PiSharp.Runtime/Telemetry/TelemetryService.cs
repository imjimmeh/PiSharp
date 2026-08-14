using System.Diagnostics.Metrics;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Extensions;

namespace PiSharp.Runtime.Telemetry;

/// <summary>
/// Central telemetry service bridging the OTel-native emit surface
/// (<see cref="ActivitySource"/> + <see cref="Meter"/> named "PiSharp") to the
/// registered <see cref="ITelemetrySink"/>s. Implements
/// <see cref="IExtensionTelemetryApi"/> so the runtime can hand it out as the
/// per-extension facade. When disabled or with no listeners/sinks attached, all
/// calls are effectively no-ops.
/// </summary>
public sealed class TelemetryService : IExtensionTelemetryApi, IDisposable
{
    public const string ActivitySourceName = "PiSharp";
    public const string MeterName = "PiSharp";

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes =
        new Dictionary<string, object?>();

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly bool _enabled;
    private readonly TimeProvider _time;
    private readonly List<ITelemetrySink> _sinks;
    private readonly ILogger _logger;

    private readonly Dictionary<string, Counter<double>> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Histogram<double>> _histograms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UpDownCounter<double>> _upDownCounters = new(StringComparer.Ordinal);

    private static readonly IDisposable EmptySpan = new EmptyDisposable();

    public TelemetryService(
        bool enabled = true,
        IReadOnlyList<ITelemetrySink>? sinks = null,
        TimeProvider? time = null,
        ILoggerFactory? loggerFactory = null)
    {
        _enabled = enabled;
        _sinks = sinks?.ToList() ?? [];
        _time = time ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger<TelemetryService>() ?? NullLogger<TelemetryService>.Instance;
        _activitySource = new ActivitySource(ActivitySourceName);
        _meter = new Meter(MeterName);

        _histograms[TelemetryInstrumentNames.TurnDuration] = _meter.CreateHistogram<double>(TelemetryInstrumentNames.TurnDuration, "s");
        _histograms[TelemetryInstrumentNames.TurnTokens] = _meter.CreateHistogram<double>(TelemetryInstrumentNames.TurnTokens, "tok");
        _histograms[TelemetryInstrumentNames.ToolDuration] = _meter.CreateHistogram<double>(TelemetryInstrumentNames.ToolDuration, "s");
        _histograms[TelemetryInstrumentNames.ExtensionLoadDuration] = _meter.CreateHistogram<double>(TelemetryInstrumentNames.ExtensionLoadDuration, "s");

        _counters[TelemetryInstrumentNames.ToolCalls] = _meter.CreateCounter<double>(TelemetryInstrumentNames.ToolCalls);
        _counters[TelemetryInstrumentNames.ToolFailures] = _meter.CreateCounter<double>(TelemetryInstrumentNames.ToolFailures);
        _counters[TelemetryInstrumentNames.ToolRetries] = _meter.CreateCounter<double>(TelemetryInstrumentNames.ToolRetries);
        _counters[TelemetryInstrumentNames.Compactions] = _meter.CreateCounter<double>(TelemetryInstrumentNames.Compactions);
        _counters[TelemetryInstrumentNames.ExtensionLoads] = _meter.CreateCounter<double>(TelemetryInstrumentNames.ExtensionLoads);
        _counters[TelemetryInstrumentNames.AttachCount] = _meter.CreateCounter<double>(TelemetryInstrumentNames.AttachCount);

        _upDownCounters[TelemetryInstrumentNames.SessionActive] = _meter.CreateUpDownCounter<double>(TelemetryInstrumentNames.SessionActive);
        _upDownCounters[TelemetryInstrumentNames.TurnActive] = _meter.CreateUpDownCounter<double>(TelemetryInstrumentNames.TurnActive);
    }

    /// <summary>True when metric/event records are currently collected and routed to sinks.</summary>
    public bool Enabled => _enabled;

    public IDisposable StartSpan(string name, ExtensionSpanKind kind = ExtensionSpanKind.Internal,
        IReadOnlyDictionary<string, object?>? attributes = null)
    {
        if (!_enabled) return EmptySpan;

        var activityKind = kind switch
        {
            ExtensionSpanKind.Server => ActivityKind.Server,
            ExtensionSpanKind.Client => ActivityKind.Client,
            ExtensionSpanKind.Producer => ActivityKind.Producer,
            ExtensionSpanKind.Consumer => ActivityKind.Consumer,
            _ => ActivityKind.Internal
        };

        Activity? activity;
        if (attributes is null)
        {
            activity = _activitySource.StartActivity(name, activityKind);
        }
        else
        {
            var tags = new ActivityTagsCollection(ToTags(attributes));
            activity = _activitySource.StartActivity(name, activityKind, default(ActivityContext), tags);
        }

        return activity ?? EmptySpan;
    }

    public void SetAttribute(string name, object? value)
    {
        if (!_enabled) return;
        Activity.Current?.SetTag(name, value);
    }

    public void EmitEvent(string name, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        if (!_enabled) return;
        Activity.Current?.AddEvent(new ActivityEvent(name, _time.GetUtcNow(), ToActivityTags(attributes)));
        RecordEventToSinks(new TelemetryEventRecord(name, _time.GetUtcNow(), attributes ?? EmptyAttributes));
    }

    public void RecordMetric(string name, double value, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        if (!_enabled) return;
        var tags = ToTags(attributes);
        if (_histograms.TryGetValue(name, out var histogram))
        {
            histogram.Record(value, tags);
        }
        else if (_upDownCounters.TryGetValue(name, out var upDown))
        {
            upDown.Add(value, tags);
        }
        RecordMetricToSinks(new TelemetryMetricRecord(name, value, _time.GetUtcNow(), attributes ?? EmptyAttributes));
    }

    public void IncrementCounter(string name, double amount = 1, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        if (!_enabled) return;
        var tags = ToTags(attributes);
        if (_counters.TryGetValue(name, out var counter))
        {
            counter.Add(amount, tags);
        }
        RecordMetricToSinks(new TelemetryMetricRecord(name, amount, _time.GetUtcNow(), attributes ?? EmptyAttributes));
    }

    public void RecordException(Exception exception, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        if (!_enabled || exception is null) return;
        Activity.Current?.AddException(exception);
        EmitEvent("exception", Merge(new Dictionary<string, object?>
        {
            ["exception.message"] = exception.Message,
            ["exception.type"] = exception.GetType().FullName,
        }, attributes));
    }

    public void Flush()
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Flush();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry sink '{Sink}' failed to flush.", sink.Name);
            }
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
        _activitySource.Dispose();
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry sink '{Sink}' failed to dispose.", sink.Name);
            }
        }
    }

    private void RecordMetricToSinks(TelemetryMetricRecord metric)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.RecordMetric(metric);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry sink '{Sink}' failed to record metric.", sink.Name);
            }
        }
    }

    private void RecordEventToSinks(TelemetryEventRecord evt)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.RecordEvent(evt);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry sink '{Sink}' failed to record event.", sink.Name);
            }
        }
    }

    private static KeyValuePair<string, object?>[] ToTags(IReadOnlyDictionary<string, object?>? attributes)
        => attributes is null ? [] : attributes.Where(kv => kv.Value is not null).ToArray();

    private static ActivityTagsCollection ToActivityTags(IReadOnlyDictionary<string, object?>? attributes)
    {
        var tags = new ActivityTagsCollection();
        if (attributes is null) return tags;
        foreach (var (key, value) in attributes)
        {
            if (value is null) continue;
            tags[key] = ToTagValue(value);
        }

        return tags;
    }

    private static object? ToTagValue(object value) => value switch
    {
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or System.DateTime or System.DateTimeOffset
            or System.TimeSpan => value,
        _ => value.ToString() ?? string.Empty
    };

    private static IReadOnlyDictionary<string, object?> Merge(
        Dictionary<string, object?> first, IReadOnlyDictionary<string, object?>? second)
    {
        if (second is null) return first;
        foreach (var (key, value) in second)
        {
            first[key] = value;
        }

        return first;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
