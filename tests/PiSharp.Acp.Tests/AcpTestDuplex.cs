using System.Text;
using System.Threading.Channels;

namespace PiSharp.Acp.Tests;

/// <summary>
/// Channel-backed in-memory stdio pair for driving <see cref="AcpServer"/> interactively with
/// deterministic ordering (send a request, wait until the expected notification/response is
/// emitted, then send the next line). Mirrors how a real ACP client interleaves requests and
/// responses.
/// </summary>
public sealed class AcpTestDuplex : IDisposable
{
    private readonly Channel<string> _toServer = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _fromServer = Channel.CreateUnbounded<string>();

    public TextReader Input { get; }
    public TextWriter Output { get; }

    /// <summary>Diagnostic: number of lines the server has read from the input side.</summary>
    public long ServerReadCount => ((DuplexReader)Input).ReadCount;

    public AcpTestDuplex()
    {
        Input = new DuplexReader(_toServer);
        Output = new DuplexWriter(_fromServer);
    }

    public Task SendAsync(string line) => _toServer.Writer.WriteAsync(line).AsTask();
    /// <summary>Signals EOF on the input side so the server read loop terminates.</summary>
    public void CompleteInput() => _toServer.Writer.TryComplete();

    public void Dispose()
    {
        _toServer.Writer.TryComplete();
        _fromServer.Writer.TryComplete();
    }

    /// <summary>Waits for the next emitted line (notification or response).</summary>
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try { return await _fromServer.Reader.ReadAsync(cancellationToken).AsTask(); }
        catch (ChannelClosedException) { return null; }
    }

    /// <summary>Reader that yields whole lines to the server.</summary>
    private sealed class DuplexReader(Channel<string> channel) : TextReader
    {
        private string? _remaining;
        public long ReadCount;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            try { return await channel.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

        public override int Read()
        {
            if (_remaining is null || _remaining.Length == 0)
            {
                if (!channel.Reader.TryRead(out var line)) return -1;
                _remaining = line;
            }
            var ch = _remaining[0];
            _remaining = _remaining[1..];
            return ch;
        }

        public override int Read(char[] buffer, int index, int count) => ReadText(buffer, index, count);
        public override int Read(Span<char> buffer) => ReadText(buffer);

        private int ReadText(char[] buffer, int index, int count)
            => ReadText(buffer.AsSpan(index, count));

        private int ReadText(Span<char> buffer)
        {
            if (buffer.Length == 0 || _remaining is not { Length: > 0 })
                return 0;
            var copy = Math.Min(buffer.Length, _remaining.Length);
            _remaining.AsSpan(0, copy).CopyTo(buffer);
            _remaining = _remaining[copy..];
            return copy;
        }
    }

    /// <summary>Writer that buffers text and pushes each line to the client channel.</summary>
    private sealed class DuplexWriter(Channel<string> channel) : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n') { FlushLine(); }
            else { _buffer.Append(value); }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var ch in value) Write(ch);
        }

        public override void WriteLine(string? value)
        {
            if (value is not null) _buffer.Append(value);
            FlushLine();
        }

        public override void Flush() { }

        private void FlushLine()
        {
            var line = _buffer.ToString();
            _buffer.Clear();
            channel.Writer.TryWrite(line);
        }
    }
}
