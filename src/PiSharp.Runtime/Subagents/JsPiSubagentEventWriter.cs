using PiSharp.Agent.Serialization;

namespace PiSharp.Runtime.Subagents;

/// <summary>
/// Writes translated JS Pi subagent event objects as JSONL to a <see cref="TextWriter"/>.
/// </summary>
public sealed class JsPiSubagentEventWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;
    private bool _disposed;

    /// <summary>
    /// Initializes a new writer that emits JSONL to <paramref name="writer"/>.
    /// </summary>
    /// <param name="writer">The target writer. Must not be null.</param>
    /// <param name="leaveOpen">If true, the underlying writer is not disposed when this instance is disposed.</param>
    public JsPiSubagentEventWriter(TextWriter writer, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _leaveOpen = leaveOpen;
    }

    public void Write(IEnumerable<object> events)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var evt in events)
            _writer.WriteLine(AgentJsonSerializer.Serialize(evt));
    }

    public async Task WriteAsync(IEnumerable<object> events, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var evt in events)
        {
            ct.ThrowIfCancellationRequested();
            await _writer.WriteLineAsync(AgentJsonSerializer.Serialize(evt).AsMemory(), ct);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_leaveOpen)
            _writer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_leaveOpen)
            await _writer.DisposeAsync();
    }
}
