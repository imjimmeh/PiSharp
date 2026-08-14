using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using PiSharp.Server.Serialization;
using Xunit;

namespace PiSharp.Server.Tests;

/// <summary>
/// P25 C6: the daemon telemetry command surface — <c>get_metrics</c> serves the shared
/// <see cref="PiSharp.Server.Runtime.TelemetryMetricsAggregator"/> snapshot (disabled tier when
/// telemetry is off; aggregated counters when on), and <c>get_session_stats</c> reports a live
/// session's counters from its retained event log.
/// </summary>
public sealed class TelemetryCommandsIntegrationTests
{
    private const string ApiKey = "itest-key";

    [Fact]
    public async Task GetMetrics_TelemetryDisabled_ReturnsDisabledSnapshot()
    {
        await using var host = new PiServerHost(new PiServerHostOptions { ApiKey = ApiKey, IdleTimeout = TimeSpan.FromHours(1) });
        await host.StartAsync(0);
        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        var response = await client.SendCommandAsync(new { id = "m1", type = ServerCommandTypes.GetMetrics });
        AssertSuccess(response);
        var data = response.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, data.GetProperty("turnCount").GetInt32());
        Assert.Equal(0, data.GetProperty("tokenCountIn").GetInt32());
        Assert.Empty(data.GetProperty("byModel").EnumerateArray());
        Assert.Empty(data.GetProperty("byTool").EnumerateArray());
        Assert.Empty(data.GetProperty("recentJournalLines").EnumerateArray());
    }

    [Fact]
    public async Task GetMetrics_TelemetryEnabled_ReflectsAggregatedRecords()
    {
        await using var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            TelemetryEnabled = true,
        });
        await host.StartAsync(0);

        var now = DateTimeOffset.UtcNow;
        host.Metrics.RecordEvent(new TelemetryEventRecord("turn_ended", now, Attrs(
            ("latencyMs", 1000L), ("tokensIn", 10L), ("tokensOut", 5L), ("tokensCache", 0L), ("model", "anthropic/claude-sonnet-4-5"))));
        host.Metrics.RecordEvent(new TelemetryEventRecord("turn_ended", now, Attrs(
            ("latencyMs", 3000L), ("tokensIn", 20L), ("tokensOut", 8L), ("tokensCache", 2L), ("model", "anthropic/claude-sonnet-4-5"))));
        host.Metrics.RecordMetric(new TelemetryMetricRecord("pisharp.tool.calls", 1, now, Attrs(("tool", "bash"))));
        host.Metrics.RecordMetric(new TelemetryMetricRecord("pisharp.tool.failures", 1, now, Attrs(("tool", "bash"), ("error", "exit 1"))));
        host.Metrics.RecordMetric(new TelemetryMetricRecord("pisharp.tool.retries", 1, now, Attrs(("tool", "bash"))));
        host.Metrics.RecordMetric(new TelemetryMetricRecord("pisharp.tool.duration", 0.5, now, Attrs(("tool", "bash"))));
        host.Metrics.RecordMetric(new TelemetryMetricRecord("pisharp.compactions", 1, now, Attrs()));

        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);
        var response = await client.SendCommandAsync(new { id = "m2", type = ServerCommandTypes.GetMetrics });
        AssertSuccess(response);
        var data = response.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, data.GetProperty("turnCount").GetInt32());
        Assert.Equal(30, data.GetProperty("tokenCountIn").GetInt32());
        Assert.Equal(13, data.GetProperty("tokenCountOut").GetInt32());
        Assert.Equal(2, data.GetProperty("tokenCountCache").GetInt32());
        Assert.Equal(2000, data.GetProperty("turnLatencyAvgMs").GetInt32());
        Assert.Equal(3000, data.GetProperty("turnLatencyP95Ms").GetInt32());
        Assert.Equal(1, data.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(1, data.GetProperty("toolFailureCount").GetInt32());
        Assert.Equal(1, data.GetProperty("toolRetryCount").GetInt32());
        Assert.Equal(1, data.GetProperty("compactionCount").GetInt32());

        var model = Assert.Single(data.GetProperty("byModel").EnumerateArray());
        Assert.Equal("anthropic/claude-sonnet-4-5", model.GetProperty("model").GetString());
        Assert.Equal(2, model.GetProperty("turns").GetInt32());
        Assert.Equal(30, model.GetProperty("tokensIn").GetInt32());

        var tool = Assert.Single(data.GetProperty("byTool").EnumerateArray());
        Assert.Equal("bash", tool.GetProperty("tool").GetString());
        Assert.Equal(2, tool.GetProperty("calls").GetInt32());
        Assert.Equal(1, tool.GetProperty("failures").GetInt32());
        Assert.Equal(1, tool.GetProperty("retries").GetInt32());
        Assert.Equal(500, tool.GetProperty("avgDurationMs").GetInt32());
    }

    [Fact]
    public async Task GetSessionStats_TracksRetriesFromRetainedEventLog()
    {
        await using var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
            TelemetryEnabled = true,
            RunCommandAsync = (context, text, options, ct) =>
            {
                context.Session.EmitEvent(AgentSessionEvent.FromOwn(new AgentHarnessOwnEvent.AutoRetryStart(1, 3, 100, "upstream 429")));
                return Task.FromResult(new ServerCommandResult(true, text));
            },
        });
        await host.StartAsync(0);
        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        var create = await client.SendCommandAsync(CreateFrame());
        AssertSuccess(create);
        var sessionId = create.RootElement.GetProperty("data").GetProperty("serverSessionId").GetString()!;

        var prompt1 = await client.SendCommandAsync(new { id = "p1", type = ServerCommandTypes.Prompt, serverSessionId = sessionId, text = "retry me" });
        AssertSuccess(prompt1);
        var prompt2 = await client.SendCommandAsync(new { id = "p2", type = ServerCommandTypes.Prompt, serverSessionId = sessionId, text = "retry again" });
        AssertSuccess(prompt2);

        var stats = await client.SendCommandAsync(new { id = "s1", type = ServerCommandTypes.GetSessionStats, serverSessionId = sessionId });
        AssertSuccess(stats);
        var data = stats.RootElement.GetProperty("data");
        Assert.Equal(sessionId, data.GetProperty("serverSessionId").GetString());
        Assert.Equal(2, data.GetProperty("retries").GetInt32());
        Assert.Equal(0, data.GetProperty("toolCalls").GetInt32());
        Assert.Equal(0, data.GetProperty("toolFailures").GetInt32());
        Assert.Equal(0, data.GetProperty("messages").GetInt32());
        Assert.True(data.TryGetProperty("startedAt", out _));
        Assert.True(data.TryGetProperty("lastActivityAt", out _));
    }

    [Fact]
    public async Task GetSessionStats_MissingSessionId_Fails()
    {
        await using var host = new PiServerHost(new PiServerHostOptions { ApiKey = ApiKey, IdleTimeout = TimeSpan.FromHours(1) });
        await host.StartAsync(0);
        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        var response = await client.SendCommandAsync(new { id = "s9", type = ServerCommandTypes.GetSessionStats });
        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
    }

    // --- helpers ---

    private static IReadOnlyDictionary<string, object?> Attrs(params (string Key, object? Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    private static Uri HostUri(PiServerHost host) => new($"ws://127.0.0.1:{host.Port}/ws");

    /// <summary>A <c>create_session</c> frame whose runtime is stripped down so the bootstrap never
    /// reaches for an LLM provider or on-disk extensions.</summary>
    private static object CreateFrame() => new
    {
        id = "create",
        type = ServerCommandTypes.CreateSession,
        cwd = Path.GetTempPath(),
        sessionsRoot = Path.Combine(Path.GetTempPath(), "pisharp-telemetry-itest-" + Guid.NewGuid().ToString("N")),
        noTools = true,
        noBuiltinTools = true,
        noExtensions = true,
        noSkills = true,
        noPromptTemplates = true,
        noThemes = true,
        noContextFiles = true,
    };

    private static void AssertSuccess(JsonDocument response)
    {
        Assert.True(
            response.RootElement.GetProperty("success").GetBoolean(),
            "command failed: " + (response.RootElement.TryGetProperty("error", out var err)
                ? err.GetProperty("message").GetString()
                : response.RootElement.GetRawText()));
    }

    private sealed class RawClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _readTask;
        private int _disposed;

        public async Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
        {
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            await _socket.ConnectAsync(uri, ct);
            _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
        }

        public async Task<JsonDocument> SendCommandAsync(object frame, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(frame, ServerJsonSerializer.Options);
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Command frame requires an 'id'.");
            var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    return await tcs.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No response for command id '{id}'.");
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    string? json;
                    using (var stream = new MemoryStream())
                    {
                        ValueWebSocketReceiveResult result;
                        do
                        {
                            result = await _socket.ReceiveAsync((Memory<byte>)buffer, ct);
                            if (result.MessageType == WebSocketMessageType.Close) return;
                            stream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);
                        json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                    }

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(json); }
                    catch (JsonException) { continue; }

                    var type = doc.RootElement.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                    if (type == "response")
                    {
                        var id = doc.RootElement.GetProperty("id").GetString();
                        if (id is not null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(doc);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            try { if (_socket.State == WebSocketState.Open) await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { /* best-effort teardown */ }
            _socket.Dispose();
            if (_readTask is not null) await _readTask;
            _cts.Dispose();
        }
    }
}
