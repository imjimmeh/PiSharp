using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.Process;

namespace PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

/// <summary>
/// Owns one long-lived server/adapter child process and its framed JSON-RPC connection:
/// spawn, request/notify plumbing, inbound canned-response handler, crash faulting, and
/// graceful shutdown (best-effort <c>shutdown</c>/<c>exit</c> notifications, then kill-tree).
/// </summary>
public sealed class ManagedRpcServer : IAsyncDisposable
{
    private readonly IServerProcess _process;
    private readonly FramedJsonRpcConnection _connection;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private readonly ILogger _logger;
    private Func<InboundRpcMessage, CancellationToken, Task<object?>>? _inboundHandler;
    private int _disposed;

    /// <summary>Identifier used for diagnostics/restart (the language or adapter id).</summary>
    public string Key { get; }

    /// <summary>The argv used to spawn; retained for diagnostics/restart.</summary>
    public IReadOnlyList<string> Command { get; }

    public bool HasExited => _process.HasExited;

    public IReadOnlyList<string> RecentStandardError => _stderrLines.ToArray();

    public event Action<string?>? StandardErrorReceived;

    /// <summary>
    /// Spawns the server process and begins pumping. The first request is expected to be
    /// the protocol handshake (LSP <c>initialize</c> / DAP <c>initialize</c>).
    /// </summary>
    public ManagedRpcServer(
        string key,
        IReadOnlyList<string> command,
        IServerProcessFactory processFactory,
        ILoggerFactory? loggerFactory = null)
        : this(key, command, processFactory, environment: null, workingDirectory: null, RpcFrameShape.JsonRpc, loggerFactory)
    {
    }

    /// <summary>
    /// Creates the server with environment/working-directory overrides and a wire-message
    /// shape (JSON-RPC for LSP; DAP for debug adapters).
    /// </summary>
    public ManagedRpcServer(
        string key,
        IReadOnlyList<string> command,
        IServerProcessFactory processFactory,
        IReadOnlyDictionary<string, object?>? environment,
        string? workingDirectory,
        RpcFrameShape shape,
        ILoggerFactory? loggerFactory = null)
    {
        Key = key;
        Command = command.ToArray();
        _logger = loggerFactory?.CreateLogger<ManagedRpcServer>() ?? NullLogger<ManagedRpcServer>.Instance;

        if (command.Count == 0) throw new ArgumentException("A server command is required.", nameof(command));

        var startInfo = new ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
        };
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value?.ToString() ?? string.Empty;
            }
        }

        for (var i = 1; i < command.Count; i++)
        {
            startInfo.ArgumentList.Add(command[i]);
        }

        _process = processFactory.Start(startInfo);
        _process.StandardErrorReceived += OnStandardErrorReceived;
        _process.BeginErrorReadLine();

        _connection = new FramedJsonRpcConnection(_process.StandardOutput, _process.StandardInput, loggerFactory, shape);
        _pumpTask = _connection.PumpAsync(HandleInboundAsync, _pumpCts.Token);
        _logger.LogInformation("rpc server '{Key}' started: {Command}", Key, string.Join(' ', Command));
    }

    public Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken ct = default)
        => _connection.RequestAsync(method, parameters, ct);

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken ct = default)
        => _connection.NotifyAsync(method, parameters, ct);

    /// <summary>Raw passthrough request with explicit id (used by the lsp tool's op=request escape hatch).</summary>
    public Task<JsonElement> RequestRawAsync(string method, JsonElement? parameters, string? id, CancellationToken ct = default)
        => _connection.RequestRawAsync(method, parameters, id, ct);

    /// <summary>
    /// Registers the canned inbound handler (last writer wins): servers send requests back
    /// at the client (workspace/configuration, window/showMessage logging,
    /// client/registerCapability no-ops); the handler answers them without a real client
    /// being present. Returning a <see cref="JsonRpcError"/> answers with an error response;
    /// any other non-null value is the result; null means "notification, no answer".
    /// </summary>
    public void EnqueueInboundHandler(Func<InboundRpcMessage, CancellationToken, Task<object?>> handler)
    {
        _inboundHandler = handler;
    }

    /// <summary>
    /// Graceful stop: best-effort <c>shutdown</c>/<c>exit</c> notifications, then
    /// <c>Kill(entireProcessTree: true)</c> after a short grace period regardless of peer
    /// behavior. Idempotent.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            await _connection.NotifyAsync("shutdown", null, ct).ConfigureAwait(false);
            await Task.WhenAny(
                _connection.NotifyAsync("exit", null, ct),
                Task.Delay(TimeSpan.FromMilliseconds(500), ct)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "Graceful shutdown notification failed for '{Key}'; killing.", Key);
        }

        await _pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Pump exit during shutdown is always expected (EOF, IO, or cancellation).
            _logger.LogDebug(exception, "Pump exited while stopping '{Key}'.", Key);
        }

        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* process already gone */ }
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        _pumpCts.Dispose();
        _logger.LogInformation("rpc server '{Key}' stopped.", Key);
    }

    private async Task<object?> HandleInboundAsync(InboundRpcMessage message, CancellationToken ct)
    {
        if (message.IsNotification)
        {
            _logger.LogDebug("rpc server '{Key}' received notification {Method}", Key, message.Method);
        }
        else
        {
            _logger.LogDebug("rpc server '{Key}' received request {Method}", Key, message.Method);
        }

        var handler = _inboundHandler;
        if (handler is not null)
        {
            return await handler(message, ct).ConfigureAwait(false);
        }

        _logger.LogWarning("rpc server '{Key}' received unhandled request {Method}; answering -32601.", Key, message.Method);
        return new JsonRpcError(-32601, $"Method not found: {message.Method}");
    }

    private void OnStandardErrorReceived(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        while (_stderrLines.Count >= 50) _stderrLines.TryDequeue(out _);
        _stderrLines.Enqueue(line);
        _logger.LogDebug("rpc server '{Key}' stderr: {Line}", Key, line);
        StandardErrorReceived?.Invoke(line);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
