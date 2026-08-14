using System.Text;
using System.Text.Json;

namespace PiSharp.Plugins.Lsp.Tests;

/// <summary>Which wire envelope the scripted peer speaks.</summary>
public enum WireProtocol
{
    /// <summary>JSON-RPC 2.0 (<c>id</c>/<c>method</c>/<c>params</c>).</summary>
    LspJsonRpc,

    /// <summary>DAP base protocol (<c>seq</c>/<c>type</c>/<c>command</c>/<c>arguments</c>).</summary>
    Dap,
}

public sealed record ReceivedWireRequest(
    string MethodOrCommand,
    JsonElement ParamsOrArguments,
    int IdOrSeq,
    bool IsNotification);

/// <summary>
/// A scripted LSP/DAP peer over the in-memory fake process pipes: reads Content-Length
/// framed messages, records them, and answers via <see cref="OnRequest"/> (return a
/// <see cref="JsonElement"/>/object for a result/body, a
/// <c>PiSharp.Plugins.ProtocolJsonRpc.JsonRpc.JsonRpcError</c> for an error, or null for
/// unanswered notifications). Requests are recorded synchronously and answered
/// concurrently (a slow responder must not block later requests — the real connection
/// dispatches inbound work the same way). Tests assert on <see cref="Received"/> and push
/// events with <see cref="SendEventAsync"/>.
public sealed class ScriptedWireServer
{

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly FakeServerProcess _process;
    private readonly WireProtocol _protocol;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Task? _pumpTask;
    private int _nextSeq;
    private int _disposed;

    public ScriptedWireServer(FakeServerProcess process, WireProtocol protocol)
    {
        _process = process;
        _protocol = protocol;
    }

    public List<ReceivedWireRequest> Received { get; } = [];

    /// <summary>Client→server response frames (id/request_seq, success flag, body).</summary>
    public List<(int Id, bool Success, JsonElement Body)> ReceivedResponses { get; } = [];

    /// <summary>
    /// (method/command, params/arguments, id/seq) → response payload. Returning null leaves
    /// the request unanswered.
    /// </summary>
    public Func<string, JsonElement, int, Task<object?>>? OnRequest { get; set; }

    /// <summary>Invoked for notifications (LSP).</summary>
    public Action<string, JsonElement>? OnNotification { get; set; }

    public void Start()
    {
        _pumpTask = PumpAsync(_cts.Token);
    }

    public Task SendEventAsync(string name, object? body = null)
    {
        if (_protocol == WireProtocol.LspJsonRpc)
        {
            throw new InvalidOperationException("Events are a DAP wire concept; use the OnRequest handler for LSP.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", NextSeq());
            writer.WriteString("type", "event");
            writer.WriteString("event", name);
            if (body is not null)
            {
                writer.WritePropertyName("body");
                JsonSerializer.Serialize(writer, body, SerializerOptions);
            }

            writer.WriteEndObject();
        }

        return WriteFrameAsync(stream.ToArray());
    }

    public Task SendRawAsync(string json) => WriteFrameAsync(Encoding.UTF8.GetBytes(json));

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement.Clone();
                if (_protocol == WireProtocol.LspJsonRpc)
                {
                    DispatchLspAsync(root, ct);
                }
                else
                {
                    DispatchDapAsync(root, ct);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or EndOfStreamException)
        {
            // Client closed the pipes or the test cancelled: pump is done.
        }
    }

    private void DispatchLspAsync(JsonElement root, CancellationToken ct)
    {
        var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()
            : null;
        if (method is null)
        {
            if (root.TryGetProperty("id", out var idElement))
            {
                var responseId = ReadIntId(idElement);
                var hasResult = root.TryGetProperty("result", out var resultElement);
                ReceivedResponses.Add((responseId, hasResult, hasResult ? resultElement.Clone() : default));
            }

            return;
        }

        var parameters = root.TryGetProperty("params", out var parametersElement) ? parametersElement.Clone() : default;
        var isNotification = !root.TryGetProperty("id", out _);
        var requestId = isNotification ? 0 : ReadIntId(root.GetProperty("id"));
        Received.Add(new ReceivedWireRequest(method, parameters, requestId, isNotification));

        if (isNotification)
        {
            OnNotification?.Invoke(method, parameters);
            return;
        }

        _ = RespondAsync(async () =>
        {
            object? result = null;
            if (OnRequest is not null)
            {
                result = await OnRequest(method, parameters, requestId).ConfigureAwait(false);
            }

            if (result is not null)
            {
                await SendLspResponseAsync(requestId, result, ct).ConfigureAwait(false);
            }
        });
    }

    private void DispatchDapAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var type = typeElement.GetString();
        if (type == "response")
        {
            var requestSeq = root.TryGetProperty("request_seq", out var requestSeqElement) && requestSeqElement.TryGetInt32(out var seqValue) ? seqValue : 0;
            var success = root.TryGetProperty("success", out var successElement) && successElement.ValueKind == JsonValueKind.True;
            var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.Clone() : default;
            ReceivedResponses.Add((requestSeq, success, body));
            return;
        }

        if (type != "request")
        {
            return; // events never arrive at the adapter
        }

        var command = root.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString() ?? "?"
            : "?";
        var arguments = root.TryGetProperty("arguments", out var argumentsElement) ? argumentsElement.Clone() : default;
        var seq = root.TryGetProperty("seq", out var seqElement) && seqElement.TryGetInt32(out var seqValue2) ? seqValue2 : 0;
        Received.Add(new ReceivedWireRequest(command, arguments, seq, IsNotification: false));

        _ = RespondAsync(async () =>
        {
            object? result = null;
            if (OnRequest is not null)
            {
                result = await OnRequest(command, arguments, seq).ConfigureAwait(false);
            }

            if (result is not null)
            {
                await SendDapResponseAsync(seq, command, result, ct).ConfigureAwait(false);
            }
        });
    }

    private static async Task RespondAsync(Func<Task> respond)
    {
        try
        {
            await respond().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A responder failure must not kill the pump; the request simply goes unanswered.
        }
    }

    private async Task SendLspResponseAsync(int id, object result, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            if (result is PiSharp.Plugins.ProtocolJsonRpc.JsonRpc.JsonRpcError error)
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", error.Code);
                writer.WriteString("message", error.Message);
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("result");
                WriteResult(writer, result);
            }

            writer.WriteEndObject();
        }

        await WriteFrameAsync(stream.ToArray()).ConfigureAwait(false);
    }

    private async Task SendDapResponseAsync(int requestSeq, string command, object result, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", NextSeq());
            writer.WriteString("type", "response");
            writer.WriteNumber("request_seq", requestSeq);
            if (result is PiSharp.Plugins.ProtocolJsonRpc.JsonRpc.JsonRpcError error)
            {
                writer.WriteBoolean("success", false);
                writer.WriteString("command", command);
                writer.WriteString("message", error.Message);
            }
            else
            {
                writer.WriteBoolean("success", true);
                writer.WriteString("command", command);
                if (result is not JsonElement { ValueKind: JsonValueKind.Undefined })
                {
                    writer.WritePropertyName("body");
                    WriteResult(writer, result);
                }
            }

            writer.WriteEndObject();
        }

        await WriteFrameAsync(stream.ToArray()).ConfigureAwait(false);
    }

    private static void WriteResult(Utf8JsonWriter writer, object result)
    {
        switch (result)
        {
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            default:
                JsonSerializer.Serialize(writer, result, SerializerOptions);
                break;
        }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var headers = await ReadHeadersAsync(ct).ConfigureAwait(false);
        var length = ParseContentLength(headers);
        if (length <= 0 || length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid Content-Length {length}.");
        }

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await _process.ServerInput.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("Server input closed.");
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
                throw new InvalidDataException("Header block exceeds 16 KiB.");
            }

            var buffer = new byte[1];
            var read = await _process.ServerInput.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("Server input closed while reading headers.");
            var value = buffer[0];
            headerBytes.WriteByte(value);

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
            if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(line[(colon + 1)..].Trim(), out var length) ? length : throw new InvalidDataException("Malformed Content-Length.");
            }
        }

        throw new InvalidDataException("Missing Content-Length header.");
    }

    private async Task WriteFrameAsync(byte[] payload)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            await _process.ServerOutput.WriteAsync(header).ConfigureAwait(false);
            await _process.ServerOutput.WriteAsync(payload).ConfigureAwait(false);
            await _process.ServerOutput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static int ReadIntId(JsonElement idElement)
        => idElement.ValueKind switch
        {
            JsonValueKind.Number when idElement.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(idElement.GetString(), out var value) => value,
            _ => 0,
        };

    private int NextSeq() => Interlocked.Increment(ref _nextSeq);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync();
        if (_pumpTask is not null)
        {
            try { await _pumpTask; }
            catch (OperationCanceledException) { }
        }

        _cts.Dispose();
        _writeGate.Dispose();
    }
}
