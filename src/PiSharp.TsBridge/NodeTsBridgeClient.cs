using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.TsBridge.JsonRpc;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge;

internal sealed class NodeTsBridgeClient(
    TsBridgeOptions options,
    IBridgeProcessFactory? processFactory = null,
    ILoggerFactory? loggerFactory = null) : ITsBridgeClient
{
    private readonly IBridgeProcessFactory _processFactory = processFactory ?? new SystemBridgeProcessFactory();
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;
    private readonly ILogger _logger = loggerFactory?.CreateLogger<NodeTsBridgeClient>() ?? NullLogger<NodeTsBridgeClient>.Instance;
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _pumpCts = new();
    private IBridgeProcess? _process;
    private JsonRpcConnection? _connection;
    private Task? _pumpTask;

    public bool IsStarted => _connection is not null;

    public IReadOnlyList<string> RecentStandardError => _stderrLines.ToArray();

    public async Task StartAsync(
        Func<JsonRpcRequest, CancellationToken, Task<object?>> requestHandler,
        object initializePayload,
        CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null) return;

            var runner = options.EffectiveRunnerPath(AppContext.BaseDirectory);
            var startInfo = new ProcessStartInfo(options.NodeExecutable, runner)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
            };
            startInfo.Environment["PISHARP_CLI"] = ResolvePiSharpCliCommand();

            _process = _processFactory.Start(startInfo);
            _process.StandardErrorReceived += OnStandardErrorReceived;
            _process.BeginErrorReadLine();

            _connection = new JsonRpcConnection(_process.StandardOutput, _process.StandardInput, _loggerFactory);
            _pumpTask = _connection.PumpAsync(requestHandler, _pumpCts.Token);
            await _connection.RequestAsync("initialize", initializePayload, cancellationToken);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        => Connection().RequestAsync(method, parameters, cancellationToken);

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        => Connection().NotifyAsync(method, parameters, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _pumpCts.CancelAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        _process?.Dispose();
        _pumpCts.Dispose();
        _startGate.Dispose();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private JsonRpcConnection Connection()
        => _connection ?? throw new InvalidOperationException("TypeScript bridge is not running.");

    private void OnStandardErrorReceived(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        _stderrLines.Enqueue(line);
        _logger.LogDebug("node: {Line}", line);
    }

    private static string ResolvePiSharpCliCommand()
    {
        var baseDir = AppContext.BaseDirectory;
        var cliDll = Path.Combine(baseDir, "PiSharp.Cli.dll");
        if (File.Exists(cliDll))
            return $"dotnet exec \"{cliDll}\"";

        var cliExe = Path.Combine(baseDir, "PiSharp.Cli.exe");
        if (File.Exists(cliExe))
            return $"\"{cliExe}\"";

        return "pi";
    }
}
