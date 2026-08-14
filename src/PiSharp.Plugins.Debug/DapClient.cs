using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

namespace PiSharp.Plugins.Debug;

/// <summary>
/// Typed DAP facade over one <see cref="ManagedRpcServer"/> (per debug session). All
/// requests are DAP-shaped (<c>seq</c>/<c>command</c>/<c>arguments</c>); responses surface
/// as their <c>body</c>, and adapter events surface via <see cref="EventReceived"/>.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private readonly int _timeoutMs;
    private readonly ILogger _logger;

    public DapClient(ManagedRpcServer server, int timeoutMs = 10000, ILoggerFactory? loggerFactory = null)
    {
        Server = server;
        _timeoutMs = timeoutMs;
        _logger = loggerFactory?.CreateLogger<DapClient>() ?? NullLogger<DapClient>.Instance;
    }

    public ManagedRpcServer Server { get; }

    /// <summary>Adapter events: (event name, body) for stopped|continued|thread|output|exited|terminated|...</summary>
    public event Action<string, JsonElement>? EventReceived;

    public Task<JsonElement> InitializeAsync(CancellationToken ct = default)
        => RequestAsync("initialize", new
        {
            adapterID = "pisharp-debug",
            clientID = "pisharp-debug",
            clientName = "PiSharp debug client",
            linesStartAt1 = true,
            columnsStartAt1 = true,
            supportsVariableType = true,
            supportsVariablePaging = true,
            supportsTerminateRequest = true,
            supportsMemoryReferences = false,
        }, ct);

    public Task<JsonElement> AttachAsync(string command, JsonElement requestBody, CancellationToken ct = default)
        => RequestAsync(command, requestBody, ct);

    public Task<JsonElement> ContinueAsync(int? threadId, CancellationToken ct = default)
        => RequestAsync("continue", threadId is null ? new { } : new { threadId }, ct);

    public Task<JsonElement> PauseAsync(int threadId, CancellationToken ct = default)
        => RequestAsync("pause", new { threadId }, ct);

    public Task<JsonElement> StepAsync(int threadId, string stepType, CancellationToken ct = default)
        => RequestAsync(stepType switch
        {
            "in" => "stepIn",
            "out" => "stepOut",
            _ => "next",
        }, new { threadId }, ct);

    public Task<JsonElement> ThreadsAsync(CancellationToken ct = default)
        => RequestAsync("threads", new { }, ct);

    public Task<JsonElement> StackTraceAsync(int threadId, int? levels, CancellationToken ct = default)
        => RequestAsync("stackTrace", levels is null ? new { threadId } : new { threadId, levels }, ct);

    public Task<JsonElement> ScopesAsync(int frameId, CancellationToken ct = default)
        => RequestAsync("scopes", new { frameId }, ct);

    public Task<JsonElement> VariablesAsync(int variablesReference, int? start, int? count, CancellationToken ct = default)
        => RequestAsync("variables", start is null && count is null
            ? new { variablesReference }
            : new { variablesReference, start = start ?? 0, count = count ?? 0 }, ct);
    public Task<JsonElement> SetBreakpointsAsync(string sourcePath, IReadOnlyList<int> lines, CancellationToken ct = default)
        => RequestAsync("setBreakpoints", new
        {
            source = new { path = sourcePath },
            breakpoints = lines.Select(line => new { line }).ToArray(),
        }, ct);




    public Task<JsonElement> EvaluateAsync(string expression, int? frameId, string? context, CancellationToken ct = default)
        => RequestAsync("evaluate", frameId is null
            ? new { expression, context = context ?? "repl" }
            : new { expression, frameId, context = context ?? "repl" }, ct);

    public Task<JsonElement> DisconnectAsync(JsonElement? body, CancellationToken ct = default)
        => RequestAsync("disconnect", body is { ValueKind: JsonValueKind.Object } bodyElement ? (object)bodyElement : new { }, ct);

    internal void RouteEvent(string eventName, JsonElement body)
    {
        _logger.LogDebug("dap event '{Event}'", eventName);
        EventReceived?.Invoke(eventName, body.Clone());
    }

    private Task<JsonElement> RequestAsync(string command, object parameters, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        return Server.RequestAsync(command, parameters, timeoutCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Adapter dispose failed; process already gone.");
        }
    }
}
