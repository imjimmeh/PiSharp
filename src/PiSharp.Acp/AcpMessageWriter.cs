using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Acp;

/// <summary>
/// Single-writer JSON-RPC message gate over a <see cref="TextWriter"/> (plan §3.2). Responses
/// and notifications interleave safely because every write is serialized by a semaphore and
/// flushed before the gate is released. stdout carries only ACP messages.
/// </summary>
public sealed class AcpMessageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AcpMessageWriter(TextWriter writer) => _writer = writer;

    public Task WriteResponseAsync(object? id, object? result, CancellationToken cancellationToken = default)
        => WriteCoreAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);

    public Task WriteErrorAsync(object? id, int code, string message, object? data = null, CancellationToken cancellationToken = default)
        => WriteCoreAsync(new { jsonrpc = "2.0", id, error = new { code, message, data } }, cancellationToken);

    public Task WriteNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default)
        => WriteCoreAsync(new { jsonrpc = "2.0", method, @params }, cancellationToken);

    public Task WriteRequestAsync(object? id, string method, object? @params, CancellationToken cancellationToken = default)
        => WriteCoreAsync(new { jsonrpc = "2.0", id, method, @params }, cancellationToken);

    private async Task WriteCoreAsync(object envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, envelope.GetType(), JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
