using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Client;
using PiSharp.Compatibility.Settings;
using PiSharp.Cli.IO;
using PiSharp.Cli.Parsing;
using PiSharp.Extensions;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;

namespace PiSharp.Cli.Modes;

/// <summary>
/// P25 C7: the <c>pisharp stats</c> dashboard. The default path aggregates the local
/// <c>metrics.jsonl</c> + <c>daemon-events.jsonl</c> files (works with the daemon down);
/// <c>--live</c> queries the daemon's <c>get_metrics</c> over WS using the lease; <c>--json</c>
/// emits the raw <see cref="MetricsSnapshot"/> document; <c>--since</c> filters by age.
/// </summary>
public static class StatsMode
{
    private const int JournalTailLines = 20;
    private const string MetricsFileName = "metrics.jsonl";
    private const string JournalFileName = "daemon-events.jsonl";

    public static async Task<int> RunAsync(
        StatsCommandArgs options,
        IConsoleIO console,
        CancellationToken cancellationToken = default,
        string? homeDirectory = null,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(nameof(StatsMode));
        logger.LogInformation("Stats mode started json={Json} live={Live} since={Since}", options.Json, options.Live, options.Since);
        var paths = PiAgentPaths.FromCwd(Directory.GetCurrentDirectory(), homeDirectory);
        var logsDirectory = Path.Combine(paths.GlobalPiSharpDirectory, "logs");

        if (options.Live)
        {
            var store = new DaemonLeaseStore(paths.GlobalPiSharpDirectory);
            var lease = await store.ReadAsync(cancellationToken);
            if (lease is null)
            {
                await console.Error.WriteLineAsync("no live daemon (no usable lease)".AsMemory(), cancellationToken);
                return 1;
            }

            var snapshot = await QueryLiveSnapshotAsync(lease, loggerFactory, cancellationToken);
            if (snapshot is null)
            {
                await console.Error.WriteLineAsync($"daemon get_metrics failed for pid {lease.Pid}".AsMemory(), cancellationToken);
                return 1;
            }

            await RenderAsync(snapshot, options, console, lease, cancellationToken);
            return 0;
        }

        var snapshotFromFiles = BuildFileSnapshot(
            Path.Combine(logsDirectory, MetricsFileName),
            Path.Combine(logsDirectory, JournalFileName),
            options.Since,
            DateTimeOffset.UtcNow);
        await RenderAsync(snapshotFromFiles, options, console, lease: null, cancellationToken);
        return 0;
    }

    private static async Task<MetricsSnapshot?> QueryLiveSnapshotAsync(DaemonLease lease, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(StatsMode));
        try
        {
            await using var transport = new ClientWebSocketTransport(loggerFactory.CreateLogger<ClientWebSocketTransport>(), TimeSpan.FromSeconds(10));
            await using var connection = new ClientSessionConnection(transport, loggerFactory.CreateLogger<ClientSessionConnection>());
            await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{lease.Port}/ws"), lease.ApiKey, cancellationToken);
            var response = await connection.SendAsync(new ServerCommandEnvelope(ServerCommandTypes.GetMetrics), cancellationToken);
            if (!response.Success || response.Data is not JsonElement element)
            {
                logger.LogWarning("Daemon get_metrics request failed pid={Pid} port={Port}", lease.Pid, lease.Port);
                return null;
            }
            return JsonSerializer.Deserialize<MetricsSnapshot>(element.GetRawText(), ServerJsonSerializer.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Daemon get_metrics failed pid={Pid} port={Port}", lease.Pid, lease.Port);
            return null;
        }
    }

    /// <summary>
    /// Aggregates the local metrics JSONL into a <see cref="MetricsSnapshot"/> by replaying the
    /// records through the same <see cref="TelemetryMetricsAggregator"/> the daemon uses, so file
    /// and live output are byte-identical in shape. Missing/empty metrics file ⇒
    /// <see cref="MetricsSnapshot.Disabled"/> (the telemetry-off tier).
    /// </summary>
    internal static MetricsSnapshot BuildFileSnapshot(string metricsPath, string journalPath, TimeSpan? since, DateTimeOffset now)
    {
        var aggregator = new TelemetryMetricsAggregator(enabled: true);
        var sawRecord = false;

        if (File.Exists(metricsPath))
        {
            foreach (var line in File.ReadLines(metricsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryParseRecord(line, since, now, out var metric, out var evt))
                {
                    if (metric is not null) aggregator.RecordMetric(metric);
                    else if (evt is not null) aggregator.RecordEvent(evt);
                    sawRecord = true;
                }
            }
        }

        if (!sawRecord) return MetricsSnapshot.Disabled(now);
        return aggregator.BuildSnapshot(now, ReadJournalTail(journalPath));
    }

    private static IReadOnlyList<string> ReadJournalTail(string journalPath)
    {
        if (!File.Exists(journalPath)) return [];
        var lines = new LinkedList<string>();
        foreach (var line in File.ReadLines(journalPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lines.AddLast(line);
            if (lines.Count > JournalTailLines) lines.RemoveFirst();
        }
        return lines.ToArray();
    }

    private static bool TryParseRecord(string line, TimeSpan? since, DateTimeOffset now, out TelemetryMetricRecord? metric, out TelemetryEventRecord? evt)
    {
        metric = null;
        evt = null;
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;

        var timestamp = root.TryGetProperty("ts", out var tsElement) && tsElement.TryGetDateTimeOffset(out var parsedTs)
            ? parsedTs
            : DateTimeOffset.MinValue;
        if (since is { } minAge && timestamp < now - minAge) return false;

        var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (name is null) return false;

        var attributes = ReadAttributes(root);
        switch (kind)
        {
            case "metric":
                var value = root.TryGetProperty("value", out var valueElement) ? valueElement.GetDouble() : 0;
                metric = new TelemetryMetricRecord(name, value, timestamp, attributes);
                return true;
            case "event":
                evt = new TelemetryEventRecord(name, timestamp, attributes);
                return true;
            default:
                return false;
        }
    }

    private static IReadOnlyDictionary<string, object?> ReadAttributes(JsonElement root)
    {
        if (!root.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in attributes.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out var integer) ? (object)integer : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.Value.GetRawText()
            };
        }
        return result;
    }

    private static async Task RenderAsync(MetricsSnapshot snapshot, StatsCommandArgs options, IConsoleIO console, DaemonLease? lease, CancellationToken cancellationToken)
    {
        if (options.Json)
        {
            await console.Out.WriteLineAsync(JsonSerializer.Serialize(snapshot, ServerJsonSerializer.Options).AsMemory(), cancellationToken);
            return;
        }

        var sb = new StringBuilder();
        RenderLease(sb, lease);
        if (!snapshot.Enabled)
        {
            sb.AppendLine("telemetry: disabled — per-turn aggregates need `telemetry.enabled: true`.");
            sb.AppendLine("(daemon/journal history is still shown above when available)");
        }
        else
        {
            RenderSessions(sb, snapshot);
            RenderTurns(sb, snapshot);
            RenderTokens(sb, snapshot);
            RenderTools(sb, snapshot);
            RenderReliability(sb, snapshot);
        }

        await console.Out.WriteLineAsync(sb.ToString().AsMemory(), cancellationToken);
    }

    private static void RenderLease(StringBuilder sb, DaemonLease? lease)
    {
        sb.AppendLine("Daemon:");
        if (lease is null)
        {
            sb.AppendLine("  status: not running (file-based view)");
        }
        else
        {
            sb.AppendLine($"  status: running (pid {lease.Pid}, port {lease.Port}, version {lease.Version})");
            sb.AppendLine($"  started: {lease.StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
        }
        sb.AppendLine();
    }

    private static void RenderSessions(StringBuilder sb, MetricsSnapshot snapshot)
    {
        sb.AppendLine($"Sessions:");
        sb.AppendLine($"  active: {snapshot.SessionCount}");
        sb.AppendLine();
    }

    private static void RenderTurns(StringBuilder sb, MetricsSnapshot snapshot)
    {
        sb.AppendLine($"Turns:");
        sb.AppendLine($"  count: {snapshot.TurnCount}");
        sb.AppendLine($"  latency: avg {snapshot.TurnLatencyAvgMs:0.#} ms, p95 {snapshot.TurnLatencyP95Ms:0.#} ms");
        sb.AppendLine();
    }

    private static void RenderTokens(StringBuilder sb, MetricsSnapshot snapshot)
    {
        sb.AppendLine($"Tokens:");
        sb.AppendLine($"  in: {snapshot.TokenCountIn}, out: {snapshot.TokenCountOut}, cache: {snapshot.TokenCountCache}");
        foreach (var model in snapshot.ByModel)
        {
            sb.AppendLine($"  {model.Model}: {model.Turns} turns, {model.TokensIn + model.TokensOut + model.TokensCache} tokens, avg {model.AvgLatencyMs:0.#} ms");
        }
        sb.AppendLine();
    }

    private static void RenderTools(StringBuilder sb, MetricsSnapshot snapshot)
    {
        sb.AppendLine($"Tools:");
        sb.AppendLine($"  calls: {snapshot.ToolCallCount}, failures: {snapshot.ToolFailureCount}, retries: {snapshot.ToolRetryCount}");
        foreach (var tool in snapshot.ByTool)
        {
            sb.AppendLine($"  {tool.Tool}: {tool.Calls} calls, {tool.Failures} failures, {tool.Retries} retries, avg {tool.AvgDurationMs:0.#} ms");
        }
        sb.AppendLine();
    }

    private static void RenderReliability(StringBuilder sb, MetricsSnapshot snapshot)
    {
        sb.AppendLine("Reliability:");
        sb.AppendLine($"  compactions: {snapshot.CompactionCount}, tool retries: {snapshot.ToolRetryCount}");
        if (snapshot.RecentJournalLines.Count > 0)
        {
            sb.AppendLine("  recent daemon events:");
            foreach (var line in snapshot.RecentJournalLines) sb.AppendLine($"    {line}");
        }
    }
}
