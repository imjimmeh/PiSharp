using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Agent.Serialization;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge.JsonRpc;

public sealed class JsonRpcConnection(TextReader input, TextWriter output, ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private int _nextId;
    private readonly ILogger _logger = loggerFactory?.CreateLogger<JsonRpcConnection>() ?? NullLogger<JsonRpcConnection>.Instance;

    public async Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await using var _ = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        _logger.LogDebug($"rpc: -> request id={id} method={method}");
        var stopwatch = Stopwatch.StartNew();
        await WriteAsync(new JsonRpcRequest("2.0", method, parameters, id), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug($"rpc: .. awaiting response id={id} method={method}");
        try { return await tcs.Task.ConfigureAwait(false); }
        finally
        {
            stopwatch.Stop();
            _logger.LogDebug($"rpc: <- response id={id} method={method} duration={stopwatch.ElapsedMilliseconds}ms");
        }
    }

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        => WriteAsync(new JsonRpcRequest("2.0", method, parameters, null), cancellationToken);

    public async Task PumpAsync(Func<JsonRpcRequest, CancellationToken, Task<object?>> handleRequest, CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            string? line;
            while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("method", out var methodElement))
                {
                    var method = methodElement.GetString() ?? "unknown";
                    var reqId = root.TryGetProperty("id", out var reqIdElement) ? reqIdElement.GetRawText() : "null";
                    _logger.LogDebug($"rpc: pump handling request id={reqId} method={method}");
                    var request = root;
                    _ = HandleInboundRequestAsync(request, method, handleRequest, cancellationToken);
                }
                else if (root.TryGetProperty("id", out var id) && _pending.TryRemove(id.GetString() ?? string.Empty, out var tcs))
                {
                    _logger.LogDebug($"rpc: pump got response id={id}");
                    if (root.TryGetProperty("error", out var error)) tcs.TrySetException(new InvalidOperationException(error.GetRawText()));
                    else tcs.TrySetResult(root.GetProperty("result"));
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "JSON-RPC pump failed");
            failure = exception;
            throw;
        }
        finally
        {
            var closed = failure ?? new IOException("JSON-RPC connection closed before a response was received.");
            foreach (var pending in _pending.ToArray())
            {
                if (_pending.TryRemove(pending.Key, out var tcs)) tcs.TrySetException(closed);
            }
        }
    }

    private async Task HandleInboundRequestAsync(JsonElement root, string method, Func<JsonRpcRequest, CancellationToken, Task<object?>> handleRequest, CancellationToken cancellationToken)
    {
        var request = AgentJsonSerializer.Deserialize<JsonRpcRequest>(root.GetRawText())!;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await handleRequest(request, cancellationToken).ConfigureAwait(false);
            if (request.Id is null) return;
            if (result is JsonRpcError error) await WriteAsync(new JsonRpcResponse("2.0", request.Id, Error: error), cancellationToken).ConfigureAwait(false);
            else await WriteAsync(new JsonRpcResponse("2.0", request.Id, result), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (request.Id is not null) await WriteAsync(new JsonRpcResponse("2.0", request.Id, Error: new JsonRpcError(-32000, exception.Message)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogDebug($"rpc: pump completed request id={request.Id} method={method} duration={stopwatch.ElapsedMilliseconds}ms");
        }
    }

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(AgentJsonSerializer.Serialize(value)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public ValueTask DisposeAsync() { _writeGate.Dispose(); return ValueTask.CompletedTask; }
}
