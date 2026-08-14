using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

namespace PiSharp.Plugins.Debug;

/// <summary>
/// A DAP client over raw <see cref="Stream"/>s using the DAP base protocol framing
/// (Content-Length headers, same as LSP). This is the thin envelope layer between the
/// JSON-RPC transport (<see cref="FramedJsonRpcConnection"/>, which is intentionally
/// JSON-RPC-shaped and untouched) and the DAP wire format: outbound requests are
/// <c>{seq, type:"request", command, arguments}</c>, responses are correlated by
/// <c>request_seq</c>, and adapter events are dispatched to the pump handler.
///
/// Mirrors <c>FramedJsonRpcConnection</c>: pending requests keyed by seq in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>, a <see cref="SemaphoreSlim"/> write
/// gate, and a pump that faults every pending request when the stream closes.
/// </summary>
public sealed class DapConnection(Stream input, Stream output, ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    /// <summary>Rejects absurd <c>Content-Length</c> headers before a faulty peer can exhaust memory.</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ILogger _logger = loggerFactory?.CreateLogger<DapConnection>() ?? NullLogger<DapConnection>.Instance;
    private int _nextSeq;
    private int _disposed;

    /// <summary>
    /// Sends a DAP request and awaits the matching response. The response <c>body</c> is
    /// returned (an empty object when the adapter omits it); a <c>success: false</c>
    /// response throws <see cref="JsonRpcRemoteException"/> carrying the adapter message.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _nextSeq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        _logger.LogDebug("dap: -> request seq={Seq} command={Command}", seq, command);

        var payload = BuildRequest(seq, command, arguments);
        try
        {
            await WriteAsync(payload, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(seq, out _);
            throw;
        }

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _logger.LogDebug("dap: <- response seq={Seq} command={Command}", seq, command);
        }
    }

    /// <summary>
    /// Reads and dispatches frames until the input closes or <paramref name="ct"/> fires.
    /// Responses resolve the matching pending request; events are passed to
    /// <paramref name="handleEvent"/>; adapter-initiated requests (runInTerminal, ...) are
    /// answered with <c>success: false</c>. On close every still-pending request is faulted
    /// with an <see cref="IOException"/>.
    /// </summary>
    public async Task PumpAsync(
        Func<DapEvent, CancellationToken, Task> handleEvent,
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

                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    _logger.LogWarning("dap: ignoring frame without a string 'type' member");
                    continue;
                }

                switch (typeElement.GetString())
                {
                    case "response":
                        HandleResponse(root);
                        break;
                    case "event":
                        HandleEvent(root, handleEvent, ct);
                        break;
                    case "request":
                        HandleInboundRequest(root);
                        break;
                    default:
                        _logger.LogWarning("dap: ignoring frame with unknown type '{Type}'", typeElement.GetString());
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "DAP pump failed");
            failure = exception;
            throw;
        }
        finally
        {
            var closed = failure ?? new IOException("DAP connection closed before a response was received.");
            foreach (var pending in _pending.ToArray())
            {
                if (_pending.TryRemove(pending.Key, out var tcs)) tcs.TrySetException(closed);
            }
        }
    }

    private void HandleResponse(JsonElement root)
    {
        if (!root.TryGetProperty("request_seq", out var requestSeqElement)
            || !requestSeqElement.TryGetInt32(out var requestSeq))
        {
            _logger.LogWarning("dap: response without a numeric request_seq");
            return;
        }

        if (!_pending.TryRemove(requestSeq, out var tcs))
        {
            _logger.LogDebug("dap: response for unknown request_seq={RequestSeq}", requestSeq);
            return;
        }

        var success = root.TryGetProperty("success", out var successElement) && successElement.ValueKind == JsonValueKind.True;
        var command = root.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String ? commandElement.GetString() : null;
        var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() : null;
        var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.Clone() : EmptyObject;

        if (success)
        {
            tcs.TrySetResult(body);
        }
        else
        {
            var errorMessage = string.IsNullOrWhiteSpace(message)
                ? $"DAP request '{command}' failed."
                : $"DAP request '{command}' failed: {message}";
            tcs.TrySetException(new JsonRpcRemoteException(-32000, errorMessage, body));
        }
    }

    private void HandleEvent(JsonElement root, Func<DapEvent, CancellationToken, Task> handleEvent, CancellationToken ct)
    {
        if (!root.TryGetProperty("event", out var eventElement) || eventElement.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("dap: event without a string 'event' member");
            return;
        }

        var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.Clone() : (JsonElement?)null;
        var name = eventElement.GetString()!;
        _ = DispatchEventAsync(new DapEvent(name, body), handleEvent, ct);
    }

    private async Task DispatchEventAsync(DapEvent evt, Func<DapEvent, CancellationToken, Task> handleEvent, CancellationToken ct)
    {
        try
        {
            await handleEvent(evt, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "DAP event handler failed for event={Event}", evt.Name);
        }
    }

    private void HandleInboundRequest(JsonElement root)
    {
        if (!root.TryGetProperty("seq", out var seqElement) || !seqElement.TryGetInt32(out var seq))
        {
            _logger.LogWarning("dap: inbound request without a numeric seq");
            return;
        }

        var command = root.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString()
            : "?";
        _logger.LogDebug("dap: adapter request {Command} (answered unsupported)", command);
        _ = WriteAsync(
            BuildResponse(seq: Interlocked.Increment(ref _nextSeq), requestSeq: seq, success: false, command: command, message: $"{command} is not supported by the pisharp-debug client"),
            CancellationToken.None).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnRanToCompletion);
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
            var read = await input.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("DAP connection closed mid-frame.");
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
                throw new InvalidDataException("DAP header block exceeds 16 KiB.");
            }

            var buffer = new byte[1];
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("DAP connection closed while reading headers.");
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

        throw new InvalidDataException("DAP frame is missing the Content-Length header.");
    }

    private async Task WriteAsync(byte[] payload, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            await output.WriteAsync(header, ct).ConfigureAwait(false);
            await output.WriteAsync(payload, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static byte[] BuildRequest(int seq, string command, JsonElement? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", seq);
            writer.WriteString("type", "request");
            writer.WriteString("command", command);
            if (arguments is { } args && args.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName("arguments");
                args.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] BuildResponse(int seq, int requestSeq, bool success, string? command, string? message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", seq);
            writer.WriteString("type", "response");
            writer.WriteNumber("request_seq", requestSeq);
            writer.WriteBoolean("success", success);
            if (command is not null) writer.WriteString("command", command);
            if (message is not null) writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
