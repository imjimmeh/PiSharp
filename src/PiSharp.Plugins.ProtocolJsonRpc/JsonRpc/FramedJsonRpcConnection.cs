using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

/// <summary>
/// A JSON-RPC 2.0 / DAP client over raw <see cref="Stream"/>s using the LSP/DAP base protocol
/// framing: a header block of <c>Content-Length: N</c> lines terminated by a blank line,
/// followed by exactly N UTF-8 bytes of JSON payload.
///
/// Mirrors <c>src/PiSharp.TsBridge/JsonRpc/JsonRpcConnection.cs</c>: pending requests keyed
/// by id in a <see cref="ConcurrentDictionary{TKey,TValue}"/>, a <see cref="SemaphoreSlim"/>
/// write gate, and a pump that faults every pending request when the stream closes.
/// </summary>


public sealed class FramedJsonRpcConnection : IAsyncDisposable
{
    /// <summary>Rejects absurd <c>Content-Length</c> headers before a malicious/faulty peer can exhaust memory.</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly RpcFrameShape _shape;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private int _nextId;
    private int _disposed;

    public FramedJsonRpcConnection(
        Stream input,
        Stream output,
        ILoggerFactory? loggerFactory = null,
        RpcFrameShape shape = RpcFrameShape.JsonRpc)
    {
        _input = input;
        _output = output;
        _shape = shape;
        _logger = loggerFactory?.CreateLogger<FramedJsonRpcConnection>() ?? NullLogger<FramedJsonRpcConnection>.Instance;
    }

    /// <summary>Correlates a request by a connection-allocated id and awaits the matching response.</summary>
    public Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken ct = default)
        => RequestRawAsync(method, ToElement(parameters), id: null, ct);

    /// <summary>Fire-and-forget notification (JSON-RPC only; DAP has no notifications).</summary>
    public Task NotifyAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var payload = BuildMessage(method, ToElement(parameters), id: null);
        return WriteAsync(payload, ct);
    }

    /// <summary>
    /// Request with explicit control over the parameters and id. When <paramref name="id"/>
    /// is null a connection-allocated id is used; a caller-supplied id enables raw passthrough
    /// requests that must echo a specific id.
    /// </summary>
    public async Task<JsonElement> RequestRawAsync(string method, JsonElement? parameters, string? id, CancellationToken ct)
    {
        var requestId = id ?? AllocateId();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        _logger.LogDebug("rpc: -> request id={Id} method={Method}", requestId, method);

        var payload = BuildMessage(method, parameters, requestId);
        try
        {
            await WriteAsync(payload, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _logger.LogDebug("rpc: <- response id={Id} method={Method}", requestId, method);
        }
    }

    /// <summary>
    /// Reads and dispatches frames until the input closes or <paramref name="ct"/> fires.
    /// Responses resolve the matching pending request; inbound requests/events are passed to
    /// <paramref name="handleInbound"/> (returning an object/JsonElement for a result, a
    /// <see cref="JsonRpcError"/> for an error response, or null for unanswered notifications).
    /// On close every still-pending request is faulted with an <see cref="IOException"/>.
    /// </summary>
    public async Task PumpAsync(
        Func<InboundRpcMessage, CancellationToken, Task<object?>> handleInbound,
        CancellationToken ct = default)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement.Clone();

                if (root.TryGetProperty("method", out _))
                {
                    // JSON-RPC request/notification from the peer.
                    var message = new InboundRpcMessage(
                        Id: ReadId(root),
                        Method: root.GetProperty("method").GetString(),
                        Params: root.TryGetProperty("params", out var parameters) ? parameters.Clone() : null,
                        IsNotification: !root.TryGetProperty("id", out _));
                    _logger.LogDebug("rpc: pump handling request id={Id} method={Method}", message.Id, message.Method);
                    _ = DispatchInboundAsync(message, handleInbound, ct);
                }
                else if (root.TryGetProperty("id", out var idElement))
                {
                    ResolveJsonRpcResponse(root, NormalizeId(idElement));
                }
                else if (_shape == RpcFrameShape.Dap)
                {
                    if (root.TryGetProperty("request_seq", out var requestSeq))
                    {
                        ResolveDapResponse(root, NormalizeId(requestSeq));
                    }
                    else if (root.TryGetProperty("command", out var commandElement))
                    {
                        var message = new InboundRpcMessage(
                            Id: ReadId(root),
                            Method: commandElement.GetString(),
                            Params: root.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : null,
                            IsNotification: false);
                        _logger.LogDebug("rpc: pump handling dap request seq={Id} command={Method}", message.Id, message.Method);
                        _ = DispatchInboundAsync(message, handleInbound, ct);
                    }
                    else if (root.TryGetProperty("event", out var eventElement))
                    {
                        var message = new InboundRpcMessage(
                            Id: null,
                            Method: "event:" + eventElement.GetString(),
                            Params: root.TryGetProperty("body", out var body) ? body.Clone() : null,
                            IsNotification: true);
                        _logger.LogDebug("rpc: pump handling dap event {Method}", message.Method);
                        _ = DispatchInboundAsync(message, handleInbound, ct);
                    }
                    else
                    {
                        _logger.LogWarning("rpc: ignoring malformed DAP frame without request_seq/command/event");
                    }
                }
                else
                {
                    _logger.LogWarning("rpc: ignoring malformed frame without 'method' or 'id'");
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

    private void ResolveJsonRpcResponse(JsonElement root, string? id)
    {
        if (id is null || !_pending.TryRemove(id, out var tcs))
        {
            _logger.LogDebug("rpc: pump got response for unknown id={Id}", id);
            return;
        }

        _logger.LogDebug("rpc: pump got response id={Id}", id);
        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var codeValue) ? codeValue : -32000;
            var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "JSON-RPC error" : "JSON-RPC error";
            var data = error.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : (JsonElement?)null;
            tcs.TrySetException(new JsonRpcRemoteException(code, message, data));
        }
        else if (root.TryGetProperty("result", out var result))
        {
            tcs.TrySetResult(result.Clone());
        }
        else
        {
            tcs.TrySetException(new JsonRpcRemoteException(-32603, "JSON-RPC response has neither 'result' nor 'error'."));
        }
    }

    private void ResolveDapResponse(JsonElement root, string? requestSeq)
    {
        if (requestSeq is null || !_pending.TryRemove(requestSeq, out var tcs))
        {
            _logger.LogDebug("rpc: pump got dap response for unknown request_seq={Seq}", requestSeq);
            return;
        }

        var success = !root.TryGetProperty("success", out var successElement) || successElement.ValueKind != JsonValueKind.False;
        if (success)
        {
            var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            tcs.TrySetResult(body);
        }
        else
        {
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "DAP request failed" : "DAP request failed";
            tcs.TrySetException(new JsonRpcRemoteException(-32000, message));
        }
    }

    private async Task DispatchInboundAsync(
        InboundRpcMessage message,
        Func<InboundRpcMessage, CancellationToken, Task<object?>> handleInbound,
        CancellationToken ct)
    {
        try
        {
            var result = await handleInbound(message, ct).ConfigureAwait(false);
            if (message.IsNotification) return;
            if (result is JsonRpcError error)
            {
                await WriteAsync(BuildErrorResponse(message.Id!, message.Method, error), ct).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(BuildResponse(message.Id!, message.Method, result), ct).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Inbound handler failed for method={Method}", message.Method);
            if (!message.IsNotification)
            {
                var error = new JsonRpcError(-32000, exception.Message);
                try { await WriteAsync(BuildErrorResponse(message.Id!, message.Method, error), CancellationToken.None).ConfigureAwait(false); }
                catch { /* peer gone; nothing else to do */ }
            }
        }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var headers = await ReadHeadersAsync(ct).ConfigureAwait(false);
        var length = ParseContentLength(headers);

        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid Content-Length {length} (expected 1..{MaxFrameBytes}).");
        }

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await _input.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("JSON-RPC connection closed mid-frame.");
            offset += read;
        }

        return buffer;
    }

    private async Task<string> ReadHeadersAsync(CancellationToken ct)
    {
        var headerBytes = new MemoryStream();
        var trailing = new byte[4];
        var trailingLength = 0;

        while (true)
        {
            if (headerBytes.Length > 16 * 1024)
            {
                throw new InvalidDataException("JSON-RPC header block exceeds 16 KiB.");
            }

            var buffer = new byte[1];
            var read = await _input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("JSON-RPC connection closed while reading headers.");
            var value = buffer[0];
            headerBytes.WriteByte(value);

            // Track the last 4 bytes to detect the header terminator: a blank line,
            // which per spec is CRLFCRLF, tolerated as bare LFLF.
            if (trailingLength < 4)
            {
                trailing[trailingLength++] = value;
            }
            else
            {
                trailing[0] = trailing[1];
                trailing[1] = trailing[2];
                trailing[2] = trailing[3];
                trailing[3] = value;
            }

            var windowLength = Math.Min(trailingLength, 4);
            if (EndsWith(trailing, windowLength, "\r\n\r\n") || EndsWith(trailing, windowLength, "\n\n"))
            {
                return Encoding.ASCII.GetString(headerBytes.ToArray());
            }
        }
    }

    private static bool EndsWith(byte[] buffer, int length, string suffix)
    {
        if (length < suffix.Length) return false;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (buffer[length - suffix.Length + i] != (byte)suffix[i]) return false;
        }
        return true;
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var rawLine in headers.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(value, out var length) ? length : throw new InvalidDataException($"Malformed Content-Length header value '{value}'.");
            }
        }

        throw new InvalidDataException("JSON-RPC frame is missing the Content-Length header.");
    }

    private async Task WriteAsync(byte[] payload, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            await _output.WriteAsync(header, ct).ConfigureAwait(false);
            await _output.WriteAsync(payload, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private byte[] BuildMessage(string method, JsonElement? parameters, string? id)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (_shape == RpcFrameShape.Dap)
            {
                writer.WriteNumber("seq", int.TryParse(id, out var seq) ? seq : 0);
                writer.WriteString("type", "request");
                writer.WriteString("command", method);
                if (parameters is { } dapArguments && dapArguments.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName("arguments");
                    dapArguments.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteString("jsonrpc", "2.0");
                if (id is not null) writer.WriteString("id", id);
                writer.WriteString("method", method);
                if (parameters is { } p && p.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName("params");
                    p.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private byte[] BuildResponse(string id, string? command, object? result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (_shape == RpcFrameShape.Dap)
            {
                writer.WriteNumber("seq", int.TryParse(AllocateId(), out var seq) ? seq : 0);
                writer.WriteString("type", "response");
                writer.WriteNumber("request_seq", int.TryParse(id, out var requestSeq) ? requestSeq : 0);
                writer.WriteString("command", command ?? string.Empty);
                writer.WriteBoolean("success", true);
                if (result is not null)
                {
                    writer.WritePropertyName("body");
                    if (result is JsonElement element)
                    {
                        element.WriteTo(writer);
                    }
                    else
                    {
                        JsonSerializer.Serialize(writer, result, SerializerOptions);
                    }
                }
            }
            else
            {
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("id", id);
                writer.WritePropertyName("result");
                if (result is JsonElement element)
                {
                    element.WriteTo(writer);
                }
                else
                {
                    JsonSerializer.Serialize(writer, result, SerializerOptions);
                }
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private byte[] BuildErrorResponse(string id, string? command, JsonRpcError error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (_shape == RpcFrameShape.Dap)
            {
                writer.WriteNumber("seq", int.TryParse(AllocateId(), out var seq) ? seq : 0);
                writer.WriteString("type", "response");
                writer.WriteNumber("request_seq", int.TryParse(id, out var requestSeq) ? requestSeq : 0);
                writer.WriteString("command", command ?? string.Empty);
                writer.WriteBoolean("success", false);
                writer.WriteString("message", error.Message);
                if (error.Data is not null)
                {
                    writer.WritePropertyName("body");
                    JsonSerializer.Serialize(writer, error.Data, SerializerOptions);
                }
            }
            else
            {
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("id", id);
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", error.Code);
                writer.WriteString("message", error.Message);
                if (error.Data is not null)
                {
                    writer.WritePropertyName("data");
                    JsonSerializer.Serialize(writer, error.Data, SerializerOptions);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private string AllocateId() => Interlocked.Increment(ref _nextId).ToString();

    private static JsonElement? ToElement(object? parameters)
        => parameters is null
            ? null
            : parameters is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(parameters, SerializerOptions);

    private static string? ReadId(JsonElement root)
        => root.TryGetProperty("id", out var idElement) ? NormalizeId(idElement) : null;

    /// <summary>Canonical id form: string value, or the raw text of a numeric id.</summary>
    private static string? NormalizeId(JsonElement idElement)
        => idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Number => idElement.GetRawText(),
            _ => idElement.GetRawText(),
        };

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
