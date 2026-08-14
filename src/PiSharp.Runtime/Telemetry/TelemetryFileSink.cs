using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Extensions;

namespace PiSharp.Runtime.Telemetry;

/// <summary>
/// Appends telemetry records as JSON-lines to <c>metrics.jsonl</c> under the
/// daemon's logs directory. Written only when telemetry is enabled. Rotates the
/// file when the UTC day changes (old file retained as
/// <c>metrics.YYYYMMDD.jsonl</c>) and prunes archives older than
/// <c>retentionDays</c>. Appends directly to the target file — no temp files are
/// ever created, so a crash mid-write can only leave a partial final line.
/// </summary>
public sealed class TelemetryFileSink : ITelemetrySink
{
    private const string KindMetric = "metric";
    private const string KindEvent = "event";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly TimeProvider _time;
    private readonly JsonSerializerOptions _jsonOptions;
    private StreamWriter? _writer;
    private bool _hasFile;
    private DateTime _currentDay;

    public TelemetryFileSink(string path, int retentionDays = 30, TimeProvider? time = null)
    {
        _path = path;
        _directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        _retentionDays = Math.Max(0, retentionDays);
        _time = time ?? TimeProvider.System;
        _jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        _currentDay = _time.GetUtcNow().UtcDateTime.Date;
    }

    public string Name => "file:" + _path;

    public void RecordMetric(TelemetryMetricRecord metric)
        => Write(JsonSerializer.Serialize(new MetricLine(
            Ts(metric.Timestamp), KindMetric, metric.Name, metric.Value, metric.Attributes ?? Empty), _jsonOptions));

    public void RecordEvent(TelemetryEventRecord evt)
        => Write(JsonSerializer.Serialize(new EventLine(
            Ts(evt.Timestamp), KindEvent, evt.Name, evt.Attributes ?? Empty), _jsonOptions));

    public void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static IReadOnlyDictionary<string, object?> Empty { get; } = new Dictionary<string, object?>();

    private static string Ts(DateTimeOffset timestamp)
        => timestamp.UtcDateTime.ToString("O");

    private void Write(string line)
    {
        lock (_gate)
        {
            var day = _time.GetUtcNow().UtcDateTime.Date;
            if (_hasFile && day != _currentDay)
            {
                Rotate(_currentDay);
            }

            EnsureWriter(day);
            _writer!.WriteLine(line);
        }
    }

    private void EnsureWriter(DateTime day)
    {
        if (_writer is not null) return;
        Directory.CreateDirectory(_directory);
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false,
        };
        _currentDay = day;
        _hasFile = true;
    }

    private void Rotate(DateTime oldDay)
    {
        _writer?.Dispose();
        _writer = null;
        if (File.Exists(_path))
        {
            var archive = Path.Combine(_directory, $"metrics.{oldDay:yyyyMMdd}.jsonl");
            File.Move(_path, archive, overwrite: true);
            PruneOldArchives();
        }
    }

    private void PruneOldArchives()
    {
        if (_retentionDays <= 0) return;
        var cutoff = _time.GetUtcNow().UtcDateTime.Date.AddDays(-_retentionDays);
        foreach (var archive in Directory.EnumerateFiles(_directory, "metrics.*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(archive); // metrics.YYYYMMDD
            var suffix = name.Substring("metrics.".Length);
            if (DateTime.TryParseExact(suffix, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var date) && date < cutoff)
            {
                try
                {
                    File.Delete(archive);
                }
                catch (IOException) { /* best-effort */ }
            }
        }
    }

    private sealed record MetricLine(
        [property: JsonPropertyName("ts")] string Ts,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, object?> Attributes);

    private sealed record EventLine(
        [property: JsonPropertyName("ts")] string Ts,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, object?> Attributes);
}
