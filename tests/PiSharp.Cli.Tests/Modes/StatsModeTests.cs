using System.Text.Json;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Parsing;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

/// <summary>
/// P25 C7: <c>pisharp stats</c> — file-based aggregation arithmetic, <c>--since</c> filtering,
/// the <c>--json</c> document shape, the telemetry-disabled tier, and the journal tail.
/// </summary>
public sealed class StatsModeTests
{
    [Fact]
    public async Task Stats_Json_EmitsRawMetricsDocumentWithCorrectAggregates()
    {
        using var fixture = TempHome.WithMetrics(
            TurnEvent(timestamp: Now(-30), latencyMs: 1000, tokensIn: 10, tokensOut: 5, tokensCache: 0),
            TurnEvent(timestamp: Now(-20), latencyMs: 3000, tokensIn: 20, tokensOut: 8, tokensCache: 2),
            Metric("pisharp.tool.calls", Now(-10), value: 1, ("tool", "bash")),
            Metric("pisharp.tool.calls", Now(-9), value: 1, ("tool", "bash")),
            Metric("pisharp.tool.failures", Now(-8), value: 1, ("tool", "bash"), ("error", "exit 1")),
            Metric("pisharp.tool.retries", Now(-7), value: 1, ("tool", "bash")),
            Metric("pisharp.compactions", Now(-6), value: 1),
            Metric("pisharp.tool.duration", Now(-5), value: 0.5, ("tool", "bash"), ("result", "ok")));

        var console = new TestConsoleIO();
        var exit = await StatsMode.RunAsync(new StatsCommandArgs(Json: true), console, homeDirectory: fixture.Home);

        Assert.Equal(0, exit);
        var snapshot = JsonSerializer.Deserialize<MetricsSnapshot>(console.Output.ToString(), ServerJsonSerializer.Options)!;
        Assert.True(snapshot.Enabled);
        Assert.Equal(2, snapshot.TurnCount);
        Assert.Equal(30, snapshot.TokenCountIn);
        Assert.Equal(13, snapshot.TokenCountOut);
        Assert.Equal(2, snapshot.TokenCountCache);
        Assert.Equal(2000, snapshot.TurnLatencyAvgMs);
        Assert.Equal(3000, snapshot.TurnLatencyP95Ms);
        Assert.Equal(2, snapshot.ToolCallCount);
        Assert.Equal(1, snapshot.ToolFailureCount);
        Assert.Equal(1, snapshot.ToolRetryCount);
        Assert.Equal(1, snapshot.CompactionCount);

        var model = Assert.Single(snapshot.ByModel);
        Assert.Equal("anthropic/claude-sonnet-4-5", model.Model);
        Assert.Equal(2, model.Turns);
        Assert.Equal(2000, model.AvgLatencyMs);

        var tool = Assert.Single(snapshot.ByTool);
        Assert.Equal("bash", tool.Tool);
        Assert.Equal(2, tool.Calls);
        Assert.Equal(1, tool.Failures);
        Assert.Equal(1, tool.Retries);
        Assert.Equal(500, tool.AvgDurationMs);
    }

    [Fact]
    public async Task Stats_MissingMetricsFile_ReportsDisabledTier()
    {
        using var fixture = TempHome.Empty();

        var console = new TestConsoleIO();
        var exit = await StatsMode.RunAsync(new StatsCommandArgs(Json: true), console, homeDirectory: fixture.Home);

        Assert.Equal(0, exit);
        var snapshot = JsonSerializer.Deserialize<MetricsSnapshot>(console.Output.ToString(), ServerJsonSerializer.Options)!;
        Assert.False(snapshot.Enabled);
        Assert.Equal(0, snapshot.TurnCount);
        Assert.Empty(snapshot.ByModel);
        Assert.Empty(snapshot.ByTool);
    }

    [Fact]
    public async Task Stats_Disabled_RendersHint()
    {
        using var fixture = TempHome.Empty();

        var console = new TestConsoleIO();
        await StatsMode.RunAsync(new StatsCommandArgs(), console, homeDirectory: fixture.Home);

        var output = console.Output.ToString();
        Assert.Contains("telemetry: disabled", output);
        Assert.Contains("Daemon:", output);
    }

    [Fact]
    public async Task Stats_Since_FiltersOutOlderRecords()
    {
        using var fixture = TempHome.WithMetrics(
            TurnEvent(timestamp: Now(-5, TimeSpan.FromDays(5)), latencyMs: 9000, tokensIn: 1, tokensOut: 1, tokensCache: 0),
            TurnEvent(timestamp: Now(-30), latencyMs: 1000, tokensIn: 10, tokensOut: 5, tokensCache: 0));

        var console = new TestConsoleIO();
        await StatsMode.RunAsync(new StatsCommandArgs(Json: true, Since: TimeSpan.FromDays(1)), console, homeDirectory: fixture.Home);

        var snapshot = JsonSerializer.Deserialize<MetricsSnapshot>(console.Output.ToString(), ServerJsonSerializer.Options)!;
        Assert.Equal(1, snapshot.TurnCount);
        Assert.Equal(1000, snapshot.TurnLatencyAvgMs);
        Assert.Equal(10, snapshot.TokenCountIn);
    }

    [Fact]
    public async Task Stats_IncludesJournalTailFromDaemonEventsFile()
    {
        using var fixture = TempHome.WithMetrics(
            TurnEvent(timestamp: Now(-30), latencyMs: 1000, tokensIn: 1, tokensOut: 1, tokensCache: 0));
        fixture.WriteJournalLine("{\"event\":\"daemon_started\"}");
        fixture.WriteJournalLine("{\"event\":\"daemon_stopped\"}");

        var console = new TestConsoleIO();
        await StatsMode.RunAsync(new StatsCommandArgs(Json: true), console, homeDirectory: fixture.Home);

        var snapshot = JsonSerializer.Deserialize<MetricsSnapshot>(console.Output.ToString(), ServerJsonSerializer.Options)!;
        Assert.Equal(2, snapshot.RecentJournalLines.Count);
        Assert.Contains(snapshot.RecentJournalLines, line => line.Contains("daemon_started"));
    }

    [Fact]
    public async Task Stats_HumanReadable_RendersSections()
    {
        using var fixture = TempHome.WithMetrics(
            TurnEvent(timestamp: Now(-30), latencyMs: 1000, tokensIn: 10, tokensOut: 5, tokensCache: 0),
            Metric("pisharp.tool.calls", Now(-10), value: 1, ("tool", "bash")));

        var console = new TestConsoleIO();
        await StatsMode.RunAsync(new StatsCommandArgs(), console, homeDirectory: fixture.Home);

        var output = console.Output.ToString();
        Assert.Contains("Daemon:", output);
        Assert.Contains("Turns:", output);
        Assert.Contains("latency: avg 1000 ms", output);
        Assert.Contains("Tokens:", output);
        Assert.Contains("Tools:", output);
        Assert.Contains("Reliability:", output);
    }

    [Fact]
    public async Task Stats_Live_WithoutLease_ReturnsError()
    {
        using var fixture = TempHome.Empty();

        var console = new TestConsoleIO();
        var exit = await StatsMode.RunAsync(new StatsCommandArgs(Live: true), console, homeDirectory: fixture.Home);

        Assert.Equal(1, exit);
        Assert.Contains("no live daemon", console.ErrorOutput.ToString());
    }

    private static DateTimeOffset Now(int minutesAgo, TimeSpan? offset = null)
        => DateTimeOffset.UtcNow - TimeSpan.FromMinutes(minutesAgo) - (offset ?? TimeSpan.Zero);

    private static string TurnEvent(DateTimeOffset timestamp, double latencyMs, long tokensIn, long tokensOut, long tokensCache)
        => $"{{\"ts\":\"{timestamp:O}\",\"kind\":\"event\",\"name\":\"turn_ended\",\"attributes\":{{\"latencyMs\":{latencyMs},\"tokensIn\":{tokensIn},\"tokensOut\":{tokensOut},\"tokensCache\":{tokensCache},\"model\":\"anthropic/claude-sonnet-4-5\"}}}}";

    private static string Metric(string name, DateTimeOffset timestamp, double value, params (string Key, string Value)[] attributes)
    {
        var attrs = string.Join(",", attributes.Select(a => $"\"{a.Key}\":\"{a.Value}\""));
        return $"{{\"ts\":\"{timestamp:O}\",\"kind\":\"metric\",\"name\":\"{name}\",\"value\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"attributes\":{{{attrs}}}}}";
    }

    private sealed class TempHome : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "pisharp-stats-" + Guid.NewGuid().ToString("N"));
        private string Logs => Path.Combine(Home, ".pi", "PiSharp", "logs");
        private string MetricsPath => Path.Combine(Logs, "metrics.jsonl");

        private TempHome(bool createLogs)
        {
            if (createLogs) Directory.CreateDirectory(Logs);
        }

        public static TempHome Empty() => new(createLogs: false);

        public static TempHome WithMetrics(params string[] lines)
        {
            var fixture = new TempHome(createLogs: true);
            File.WriteAllLines(fixture.MetricsPath, lines);
            return fixture;
        }

        public void WriteJournalLine(string line)
            => File.AppendAllText(Path.Combine(Logs, "daemon-events.jsonl"), line + Environment.NewLine);

        public void Dispose()
        {
            try { Directory.Delete(Home, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }
}
