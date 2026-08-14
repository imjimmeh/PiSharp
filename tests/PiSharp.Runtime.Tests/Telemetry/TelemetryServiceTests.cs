using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;
using PiSharp.Runtime.Telemetry;
using Xunit;

namespace PiSharp.Runtime.Tests;

/// <summary>
/// P25 C2–C4: TelemetryService sink routing and no-op gating, the
/// HarnessTelemetryInstrumentor mapping of harness events onto instrument/event
/// records, and the TelemetryFileSink JSONL format (+ time-based rotation).
/// </summary>
public sealed class TelemetryServiceTests
{
    [Fact]
    public void EmitEvent_RoutesEventRecordToSink()
    {
        var sink = new FakeSink();
        using var service = new TelemetryService(enabled: true, sinks: [sink]);

        service.EmitEvent(TelemetryEventNames.ToolFailed, new Dictionary<string, object?> { ["tool"] = "bash" });

        var evt = Assert.Single(sink.Events);
        Assert.Equal(TelemetryEventNames.ToolFailed, evt.Name);
        Assert.Equal("bash", evt.Attributes["tool"]);
        Assert.Empty(sink.Metrics);
    }

    [Fact]
    public void RecordMetric_RoutesMetricRecordToSink()
    {
        var sink = new FakeSink();
        using var service = new TelemetryService(enabled: true, sinks: [sink]);

        service.RecordMetric(TelemetryInstrumentNames.ToolDuration, 0.5,
            new Dictionary<string, object?> { ["tool"] = "bash" });

        var metric = Assert.Single(sink.Metrics);
        Assert.Equal(TelemetryInstrumentNames.ToolDuration, metric.Name);
        Assert.Equal(0.5, metric.Value);
        Assert.Equal("bash", metric.Attributes["tool"]);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void IncrementCounter_RoutesMetricRecordToSink()
    {
        var sink = new FakeSink();
        using var service = new TelemetryService(enabled: true, sinks: [sink]);

        service.IncrementCounter(TelemetryInstrumentNames.ToolCalls, 2,
            new Dictionary<string, object?> { ["tool"] = "bash" });

        var metric = Assert.Single(sink.Metrics);
        Assert.Equal(TelemetryInstrumentNames.ToolCalls, metric.Name);
        Assert.Equal(2, metric.Value);
    }

    [Fact]
    public void Disabled_NoRecordAndNoException()
    {
        var sink = new FakeSink();
        using var service = new TelemetryService(enabled: false, sinks: [sink]);

        using (var span = service.StartSpan("work"))
        {
            service.SetAttribute("k", "v");
            service.EmitEvent("some_event");
            service.RecordMetric("some.metric", 1);
            service.IncrementCounter("some.counter", 1);
            service.RecordException(new InvalidOperationException("boom"));
        }

        Assert.False(service.Enabled);
        Assert.Empty(sink.Events);
        Assert.Empty(sink.Metrics);
    }

    [Fact]
    public void Instrumentor_EmitsTurnToolCompactionEventRecords()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        var sink = new FakeSink();
        using var service = new TelemetryService(enabled: true, sinks: [sink], time: time);
        var instrumentor = new HarnessTelemetryInstrumentor(service, sessionId: "s1", model: "anthropic/claude-sonnet-4-5", time: time);

        instrumentor.Handle(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("boot")));
        instrumentor.Handle(new AgentHarnessEvent.Core(new AgentEvent.TurnStart()));
        time.Advance(TimeSpan.FromMilliseconds(50));
        instrumentor.Handle(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("t1", "bash", default)));
        time.Advance(TimeSpan.FromMilliseconds(200));
        instrumentor.Handle(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("t1", "bash", "ok", false)));
        time.Advance(TimeSpan.FromMilliseconds(50));
        instrumentor.Handle(new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(
            new AssistantMessage([], Model: "anthropic/claude-sonnet-4-5", Usage: new UsageInfo(100, 20, 5)), [])));

        Assert.Contains(sink.Events, e => e.Name == TelemetryEventNames.SessionStarted);
        Assert.Contains(sink.Events, e => e.Name == TelemetryEventNames.TurnEnded);
        Assert.Contains(sink.Metrics, m => m.Name == TelemetryInstrumentNames.ToolCalls && m.Value == 1
            && m.Attributes["tool"] as string == "bash");

        var turnEnded = sink.Events.Single(e => e.Name == TelemetryEventNames.TurnEnded);
        Assert.Equal(300.0, (double)turnEnded.Attributes["latencyMs"]!, 1);
        Assert.Equal(100, (int)turnEnded.Attributes["tokensIn"]!);
        Assert.Equal(20, (int)turnEnded.Attributes["tokensOut"]!);
        Assert.Equal(5, (int)turnEnded.Attributes["tokensCache"]!);
        Assert.Equal("s1", turnEnded.Attributes["sessionId"]);
    }

    [Fact]
    public void Instrumentor_EmitsToolFailureAndRetryRecords()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        var sink = new FakeSink();
        using var service = new TelemetryService(enabled: true, sinks: [sink], time: time);
        var instrumentor = new HarnessTelemetryInstrumentor(service, sessionId: "s1", time: time);

        instrumentor.Handle(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("t2", "bash", default)));
        time.Advance(TimeSpan.FromMilliseconds(30));
        instrumentor.Handle(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("t2", "bash", "exit 1", true)));
        instrumentor.Handle(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.AutoRetryStart(2, 3, 100, "exit 1")));

        var failure = sink.Metrics.Single(m => m.Name == TelemetryInstrumentNames.ToolFailures);
        Assert.Equal(1, failure.Value);
        Assert.Equal("bash", failure.Attributes["tool"]);
        Assert.Equal("exit 1", failure.Attributes["error"]);

        Assert.Contains(sink.Events, e => e.Name == TelemetryEventNames.ToolFailed
            && e.Attributes["error"] as string == "exit 1");
        Assert.Contains(sink.Metrics, m => m.Name == TelemetryInstrumentNames.ToolRetries && m.Value == 1);
        Assert.Contains(sink.Events, e => e.Name == TelemetryEventNames.ToolRetried);
    }

    [Fact]
    public void Instrumentor_EmitsCompactionOnSuccessfulCompact()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        var sink = new FakeSink();
        using var service = new TelemetryService(enabled: true, sinks: [sink], time: time);
        var instrumentor = new HarnessTelemetryInstrumentor(service, time: time);

        instrumentor.Handle(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionStart("full")));
        instrumentor.Handle(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionEnd("full", null, Aborted: false, WillRetry: false, ErrorMessage: null)));

        Assert.Contains(sink.Metrics, m => m.Name == TelemetryInstrumentNames.Compactions && m.Value == 1);
        Assert.Contains(sink.Events, e => e.Name == TelemetryEventNames.CompactionRan);

        // Aborted compaction must not emit a record.
        instrumentor.Handle(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionEnd("full", null, Aborted: true, WillRetry: false, ErrorMessage: null)));
        Assert.Equal(1, sink.Metrics.Count(m => m.Name == TelemetryInstrumentNames.Compactions));
    }

    [Fact]
    public void TelemetryFileSink_WritesJsonlMetricAndEventRecords()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "metrics.jsonl");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        using (var sink = new TelemetryFileSink(path, retentionDays: 30, time: time))
        {
            sink.RecordMetric(new TelemetryMetricRecord(TelemetryInstrumentNames.ToolCalls, 1, time.GetUtcNow(),
                new Dictionary<string, object?> { ["tool"] = "bash" }));
            sink.RecordEvent(new TelemetryEventRecord(TelemetryEventNames.TurnEnded, time.GetUtcNow(),
                new Dictionary<string, object?> { ["latencyMs"] = 100.0 }));
            sink.Flush();
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        using var metric = JsonDocument.Parse(lines[0]);
        Assert.Equal("metric", metric.RootElement.GetProperty("kind").GetString());
        Assert.Equal(TelemetryInstrumentNames.ToolCalls, metric.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, metric.RootElement.GetProperty("value").GetDouble());
        Assert.Equal("bash", metric.RootElement.GetProperty("attributes").GetProperty("tool").GetString());

        using var evt = JsonDocument.Parse(lines[1]);
        Assert.Equal("event", evt.RootElement.GetProperty("kind").GetString());
        Assert.Equal(TelemetryEventNames.TurnEnded, evt.RootElement.GetProperty("name").GetString());
        Assert.Equal(100.0, evt.RootElement.GetProperty("attributes").GetProperty("latencyMs").GetDouble());

        // No temp/partial files are ever left behind.
        Assert.DoesNotContain(Directory.EnumerateFiles(tmp.Path), f => Path.GetExtension(f) is ".tmp" or ".part");
    }

    [Fact]
    public void TelemetryFileSink_RotatesOnDayBoundaryAndPrunesOldArchives()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "metrics.jsonl");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        using (var sink = new TelemetryFileSink(path, retentionDays: 1, time: time))
        {
            sink.RecordMetric(new TelemetryMetricRecord("m.1", 1, time.GetUtcNow(), EmptyAttrs)); // day 08-14
            time.Advance(TimeSpan.FromDays(1));
            sink.RecordMetric(new TelemetryMetricRecord("m.2", 1, time.GetUtcNow(), EmptyAttrs)); // day 08-15 → rotates
            time.Advance(TimeSpan.FromDays(1));
            sink.RecordMetric(new TelemetryMetricRecord("m.3", 1, time.GetUtcNow(), EmptyAttrs)); // day 08-16 → rotates + prunes
            sink.Flush();
        }

        var files = Directory.EnumerateFiles(tmp.Path).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Assert.Contains("metrics.jsonl", files);
        // 08-15 archive kept (== cutoff), 08-14 archive pruned by retentionDays=1.
        Assert.Contains("metrics.20260815.jsonl", files);
        Assert.DoesNotContain("metrics.20260814.jsonl", files);
    }

    private static IReadOnlyDictionary<string, object?> EmptyAttrs { get; } = new Dictionary<string, object?>();

    private sealed class FakeSink : ITelemetrySink
    {
        public List<TelemetryMetricRecord> Metrics { get; } = [];
        public List<TelemetryEventRecord> Events { get; } = [];
        public string Name => "fake";

        public void RecordMetric(TelemetryMetricRecord metric) => Metrics.Add(metric);
        public void RecordEvent(TelemetryEventRecord evt) => Events.Add(evt);
        public void Flush() { }
        public void Dispose() { }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _now.UtcTicks;

        public void Advance(TimeSpan span) => _now += span;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-telemetry-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
