using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.Process;

namespace PiSharp.Plugins.Debug;

/// <summary>
/// Owns one long-lived debug adapter child process and its DAP connection: spawn,
/// request plumbing, adapter-event dispatch, crash faulting, and kill-tree shutdown.
/// The DAP counterpart of the transport's <see cref="PiSharp.Plugins.ProtocolJsonRpc.JsonRpc.ManagedRpcServer"/>;
/// it cannot reuse that type because DAP wire envelopes (<c>seq</c>/<c>type</c>/<c>command</c>)
/// differ from JSON-RPC (<c>id</c>/<c>method</c>) — see <see cref="DapConnection"/>.
/// </summary>
public sealed class ManagedDapServer : IAsyncDisposable
{
    private readonly IServerProcess _process;
    private readonly DapConnection _connection;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private readonly ILogger _logger;
    private Func<DapEvent, CancellationToken, Task>? _eventHandler;
    private int _disposed;

    /// <summary>Identifier used for diagnostics/restart (the language or adapter id).</summary>
    public string Key { get; }

    /// <summary>The argv used to spawn; retained for diagnostics/restart.</summary>
    public IReadOnlyList<string> Command { get; }

    public bool HasExited => _process.HasExited;

    public IReadOnlyList<string> RecentStandardError => _stderrLines.ToArray();

    public event Action<string?>? StandardErrorReceived;

    /// <summary>
    /// Spawns the adapter process and begins pumping. The first request is expected to be
    /// the DAP <c>initialize</c> handshake.
    /// </summary>
    public ManagedDapServer(
        string key,
        IReadOnlyList<string> command,
        IServerProcessFactory processFactory,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, object?>? env = null,
        ILoggerFactory? loggerFactory = null)
    {
        Key = key;
        Command = command.ToArray();
        _logger = loggerFactory?.CreateLogger<ManagedDapServer>() ?? NullLogger<ManagedDapServer>.Instance;

        if (command.Count == 0) throw new ArgumentException("An adapter command is required.", nameof(command));

        var startInfo = new ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        for (var i = 1; i < command.Count; i++)
        {
            startInfo.ArgumentList.Add(command[i]);
        }

        if (env is not null)
        {
            foreach (var (name, value) in env)
            {
                startInfo.Environment[name] = value?.ToString() ?? string.Empty;
            }
        }

        _process = processFactory.Start(startInfo);
        _process.StandardErrorReceived += OnStandardErrorReceived;
        _process.BeginErrorReadLine();

        _connection = new DapConnection(_process.StandardOutput, _process.StandardInput, loggerFactory);
        _pumpTask = _connection.PumpAsync(OnEventAsync, _pumpCts.Token);
        _logger.LogInformation("dap server '{Key}' started: {Command}", Key, string.Join(' ', Command));
    }

    public Task<JsonElement> RequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default)
        => _connection.RequestAsync(command, arguments, ct);

    /// <summary>Registers the adapter-event handler (last writer wins).</summary>
    public void EnqueueEventHandler(Func<DapEvent, CancellationToken, Task> handler)
    {
        _eventHandler = handler;
    }

    /// <summary>
    /// Stops the adapter: cancels the pump, kills the process tree, disposes the process.
    /// DAP teardown is the caller's <c>disconnect</c> request; there are no
    /// <c>shutdown</c>/<c>exit</c> notifications to send (unlike LSP). Idempotent.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
        _logger.LogInformation("dap server '{Key}' stopped.", Key);
    }

    private Task OnEventAsync(DapEvent evt, CancellationToken ct)
    {
        var handler = _eventHandler;
        if (handler is null)
        {
            _logger.LogDebug("dap server '{Key}' received unhandled event {Event}", Key, evt.Name);
            return Task.CompletedTask;
        }

        _logger.LogDebug("dap server '{Key}' received event {Event}", Key, evt.Name);
        return handler(evt, ct);
    }

    private void OnStandardErrorReceived(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        while (_stderrLines.Count >= 50) _stderrLines.TryDequeue(out _);
        _stderrLines.Enqueue(line);
        _logger.LogDebug("dap server '{Key}' stderr: {Line}", Key, line);
        StandardErrorReceived?.Invoke(line);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
