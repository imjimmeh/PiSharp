using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Server.Observability.Tests;

/// <summary>
/// Dispatch-level coverage for the P25 C6 daemon observability surface:
/// <c>get_metrics</c> and <c>get_session_stats</c> wire commands on
/// <see cref="PiServerWebSocketHandler"/>, including the no-telemetry and unknown-session
/// error envelopes.
/// </summary>
public sealed class ObservabilitySurfaceTests
{
    [Fact]
    public async Task GetMetrics_NoAggregator_ReturnsDisabledSnapshot()
    {
        var handler = CreateHandler(metrics: null);

        var response = await handler.DispatchTextCommandAsync(CommandJson(ServerCommandTypes.GetMetrics));

        Assert.True(response.Success);
        var snapshot = Assert.IsType<MetricsSnapshot>(response.Data);
        Assert.False(snapshot.Enabled);
        Assert.Equal(0, snapshot.TurnCount);
        Assert.Empty(snapshot.ByModel);
        Assert.Empty(snapshot.ByTool);
    }

    [Fact]
    public async Task GetMetrics_EmptyAggregator_ReturnsZeroedEnabledSnapshot()
    {
        var aggregator = new TelemetryMetricsAggregator(enabled: true);
        var handler = CreateHandler(metrics: aggregator);

        var response = await handler.DispatchTextCommandAsync(CommandJson(ServerCommandTypes.GetMetrics));

        Assert.True(response.Success);
        var snapshot = Assert.IsType<MetricsSnapshot>(response.Data);
        Assert.True(snapshot.Enabled);
        Assert.Equal(0, snapshot.TurnCount);
        Assert.Equal(0, snapshot.ToolCallCount);
        Assert.Empty(snapshot.ByModel);
        Assert.Empty(snapshot.ByTool);
    }

    [Fact]
    public async Task GetMetrics_SeededAggregator_ReturnsSnapshotWithBreakdowns()
    {
        var aggregator = new TelemetryMetricsAggregator(enabled: true);
        aggregator.RecordEvent(new TelemetryEventRecord(
            "turn_ended",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["model"] = "model-a", ["latencyMs"] = 1000.0, ["tokensIn"] = 100L, ["tokensOut"] = 20L, ["tokensCache"] = 5L }));
        aggregator.RecordMetric(new TelemetryMetricRecord(
            "pisharp.tool.calls", 1, DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["tool"] = "bash" }));
        var handler = CreateHandler(metrics: aggregator);

        var response = await handler.DispatchTextCommandAsync(CommandJson(ServerCommandTypes.GetMetrics));

        Assert.True(response.Success);
        var snapshot = Assert.IsType<MetricsSnapshot>(response.Data);
        Assert.True(snapshot.Enabled);
        Assert.Equal(1, snapshot.TurnCount);
        Assert.Equal(100, snapshot.TokenCountIn);
        Assert.Equal(20, snapshot.TokenCountOut);
        Assert.Equal(1, snapshot.ToolCallCount);
        var byModel = Assert.Single(snapshot.ByModel);
        Assert.Equal("model-a", byModel.Model);
        Assert.Equal(1, byModel.Turns);
        var byTool = Assert.Single(snapshot.ByTool);
        Assert.Equal("bash", byTool.Tool);
        Assert.Equal(1, byTool.Calls);
    }

    [Fact]
    public async Task GetSessionStats_EmptySession_ReturnsZeroedCounters()
    {
        var (handler, live) = await CreateSessionAsync();

        var response = await handler.DispatchTextCommandAsync(CommandJson(ServerCommandTypes.GetSessionStats, live.Id));

        Assert.True(response.Success);
        var stats = Assert.IsType<SessionStats>(response.Data);
        Assert.Equal(live.Id, stats.ServerSessionId);
        Assert.Equal(live.RuntimeSessionId, stats.RuntimeSessionId);
        Assert.Equal(0, stats.Messages);
        Assert.Equal(0, stats.Turns);
        Assert.Equal(0, stats.TokensIn);
        Assert.Equal(0, stats.TokensOut);
        Assert.Equal(0, stats.ToolCalls);
        Assert.Equal(0, stats.ToolFailures);
        Assert.Equal(0, stats.Retries);
        Assert.NotNull(stats.Model);
    }

    [Fact]
    public async Task GetSessionStats_WithMessages_ReportsCounters()
    {
        var (handler, live) = await CreateSessionAsync();

        await live.Runtime.Session.AppendMessageAsync(new AssistantMessage(
            [new TextContent("ok"), ToolCall("t1", "bash")], Model: "anthropic/claude", Usage: new UsageInfo(Input: 100, Output: 50)));
        await live.Runtime.Session.AppendMessageAsync(new AssistantMessage(
            [new TextContent("ok"), ToolCall("t2", "bash")], Model: "anthropic/claude", Usage: new UsageInfo(Input: 200, Output: 25)));
        await live.Runtime.Session.AppendMessageAsync(new ToolResultMessage("t1", "bash", [new TextContent("out")], IsError: true));
        await live.Runtime.Session.AppendMessageAsync(new ToolResultMessage("t2", "bash", [new TextContent("out")], IsError: false));
        live.EmitEvent(AgentSessionEvent.FromServer("auto_retry_start", null));
        live.EmitEvent(AgentSessionEvent.FromServer("auto_retry_start", null));

        var response = await handler.DispatchTextCommandAsync(CommandJson(ServerCommandTypes.GetSessionStats, live.Id));

        Assert.True(response.Success);
        var stats = Assert.IsType<SessionStats>(response.Data);
        Assert.Equal(live.Id, stats.ServerSessionId);
        Assert.Equal(live.RuntimeSessionId, stats.RuntimeSessionId);
        Assert.Equal(4, stats.Messages);
        Assert.Equal(2, stats.Turns);
        Assert.Equal(300, stats.TokensIn);
        Assert.Equal(75, stats.TokensOut);
        Assert.Equal(2, stats.ToolCalls);
        Assert.Equal(1, stats.ToolFailures);
        Assert.Equal(2, stats.Retries);
        Assert.Equal("anthropic/claude", stats.Model);
        Assert.NotNull(stats.StartedAt);
        Assert.NotNull(stats.LastActivityAt);
    }

    [Fact]
    public async Task GetSessionStats_UnknownSession_ReturnsCommandFailed()
    {
        var handler = CreateHandler(metrics: null);

        var response = await handler.DispatchTextCommandAsync(CommandJson(ServerCommandTypes.GetSessionStats, "no-such-session"));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task GetSessionStats_MissingSessionId_ReturnsCommandFailed()
    {
        var handler = CreateHandler(metrics: null);

        var response = await handler.DispatchTextCommandAsync(CommandJson(ServerCommandTypes.GetSessionStats));

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    // --- helpers ---

    private static string CommandJson(string type, string? serverSessionId = null)
        => JsonSerializer.Serialize(new
        {
            id = "i",
            type,
            serverSessionId
        }, ServerJsonSerializer.Options);
    private static MessageContent ToolCall(string id, string name)
        => new ToolCallContent(id, name, JsonDocument.Parse("{}").RootElement.Clone());

    private static PiServerWebSocketHandler CreateHandler(TelemetryMetricsAggregator? metrics)
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        return new PiServerWebSocketHandler(
            registry,
            new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }),
            NullLogger<PiServerWebSocketHandler>.Instance,
            metrics: metrics);
    }

    private static async Task<(PiServerWebSocketHandler Handler, LiveServerSession Live)> CreateSessionAsync()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = new PiServerWebSocketHandler(
            registry,
            new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }),
            NullLogger<PiServerWebSocketHandler>.Instance,
            metrics: new TelemetryMetricsAggregator());
        var cwd = TempRoot();
        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(response.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        return (handler, live!);
    }
    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(
            repo,
            createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])),
            initial);
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }


    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-server-observability-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
