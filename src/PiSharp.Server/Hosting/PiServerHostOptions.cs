using Microsoft.Extensions.Logging;
using PiSharp.Extensions;
using PiSharp.Server.Contracts;

namespace PiSharp.Server.Hosting;

public sealed record PiServerHostOptions
{
    public required string ApiKey { get; init; }
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Logger factory used for host and runtime lifecycle diagnostics.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// When true, every runtime the host creates is wired to a daemon-shared
    /// <see cref="Runtime.TelemetryMetricsAggregator"/> so <c>get_metrics</c> returns live
    /// aggregates (P25 C6). Default false — no telemetry is collected.
    /// </summary>
    public bool TelemetryEnabled { get; init; } = false;

    /// <summary>
    /// Maximum accepted size, in bytes, of a single inbound WebSocket message frame cycle.
    /// Messages larger than this are rejected with a <c>MessageTooBig</c> close frame and never
    /// dispatched. Default 8 MiB.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum number of commands dispatched concurrently per WebSocket connection. Commands
    /// beyond this limit queue until an in-flight dispatch completes. Default 4.
    /// </summary>
    public int MaxConcurrentCommands { get; init; } = 4;

    /// <summary>Additional sinks fed alongside the metrics aggregator (e.g. the metrics.jsonl file sink).</summary>
    public IReadOnlyList<ITelemetrySink>? TelemetrySinks { get; init; }

    public Func<PiServerHostContext, string, SlashCommandExecutionOptions?, CancellationToken, Task<ServerCommandResult>>? RunCommandAsync { get; init; }
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? CompleteCommandAsync { get; init; }
    public Func<ProcessInputRequest, CancellationToken, Task<ProcessInputResult>>? ProcessInputAsync { get; init; }
    public Func<CancellationToken, Task<ServerStartupMessages>>? GetStartupMessagesAsync { get; init; }
    public Func<Action<string>, CancellationToken, Task>? PostStartupChecksAsync { get; init; }
    public Func<CancellationToken, Task<McpStatusResult>>? GetMcpStatusAsync { get; init; }
}
