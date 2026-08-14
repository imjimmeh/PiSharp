using System.Diagnostics;

namespace PiSharp.Extensions;

/// <summary>
/// Span kind classification mirroring OpenTelemetry's <c>ActivityKind</c>,
/// exposed to extension authors instead of leaking <c>System.Diagnostics</c>.
/// </summary>
public enum ExtensionSpanKind
{
    Internal,
    Server,
    Client,
    Producer,
    Consumer
}

/// <summary>
/// Per-extension telemetry emit surface. A thin facade over the runtime
/// <c>TelemetryService</c>: calls are OTel-native (<c>ActivitySource</c>/<c>Meter</c>)
/// and cost near-nothing when telemetry is disabled or no listener is attached.
/// Record/event methods also fan out to any registered <see cref="ITelemetrySink"/>s
/// (e.g. the local JSONL metrics file).
/// </summary>
public interface IExtensionTelemetryApi
{
    /// <summary>Starts a span; the returned <see cref="IDisposable"/> ends it. No-op when telemetry is disabled.</summary>
    IDisposable StartSpan(string name, ExtensionSpanKind kind = ExtensionSpanKind.Internal,
        IReadOnlyDictionary<string, object?>? attributes = null);

    /// <summary>Sets an attribute on the current span. No-op with no active span.</summary>
    void SetAttribute(string name, object? value);

    /// <summary>Emits a timestamped event on the current span, or as a root-level record when no span is active.</summary>
    void EmitEvent(string name, IReadOnlyDictionary<string, object?>? attributes = null);

    /// <summary>Records a value into the named instrument (histogram semantics).</summary>
    void RecordMetric(string name, double value, IReadOnlyDictionary<string, object?>? attributes = null);

    /// <summary>Adds <paramref name="amount"/> to the named counter instrument.</summary>
    void IncrementCounter(string name, double amount = 1, IReadOnlyDictionary<string, object?>? attributes = null);

    /// <summary>Records an exception on the current span, plus as a root-level event.</summary>
    void RecordException(Exception exception, IReadOnlyDictionary<string, object?>? attributes = null);

    /// <summary>Best-effort flush of sinks; called at session end / daemon shutdown.</summary>
    void Flush();
}

/// <summary>A single metric record routed to a telemetry sink (the JSONL file).</summary>
public sealed record TelemetryMetricRecord(
    string Name,
    double Value,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Attributes);

/// <summary>A single event record routed to a telemetry sink (the JSONL file).</summary>
public sealed record TelemetryEventRecord(
    string Name,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// Generic sink contract for telemetry records. Concrete sinks (e.g. the local
/// <c>metrics.jsonl</c> file) are separate implementations, per the provider-abstraction rule.
/// </summary>
public interface ITelemetrySink : IDisposable
{
    string Name { get; }

    void RecordMetric(TelemetryMetricRecord metric);

    void RecordEvent(TelemetryEventRecord evt);

    void Flush();
}

internal sealed class NoOpTelemetryApi : IExtensionTelemetryApi
{
    public static readonly NoOpTelemetryApi Instance = new();

    private static readonly IDisposable EmptySpan = new EmptyDisposable();

    private NoOpTelemetryApi() { }

    public IDisposable StartSpan(string name, ExtensionSpanKind kind = ExtensionSpanKind.Internal,
        IReadOnlyDictionary<string, object?>? attributes = null) => EmptySpan;

    public void SetAttribute(string name, object? value) { }

    public void EmitEvent(string name, IReadOnlyDictionary<string, object?>? attributes = null) { }

    public void RecordMetric(string name, double value, IReadOnlyDictionary<string, object?>? attributes = null) { }

    public void IncrementCounter(string name, double amount = 1, IReadOnlyDictionary<string, object?>? attributes = null) { }

    public void RecordException(Exception exception, IReadOnlyDictionary<string, object?>? attributes = null) { }

    public void Flush() { }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
