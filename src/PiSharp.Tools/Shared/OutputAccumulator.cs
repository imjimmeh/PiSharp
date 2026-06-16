using System.Text;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;

namespace PiSharp.Tools.Shared;

public sealed class OutputAccumulator
{
    private readonly IFileSystem _fileSystem;
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly int _maxRollingBytes;
    private readonly string _tempFilePrefix;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly List<byte[]> _rawChunks = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _tailText = string.Empty;
    private int _tailBytes;
    private bool _tailStartsAtLineBoundary = true;
    private int _totalRawBytes;
    private int _totalDecodedBytes;
    private int _totalLines = 1;
    private int _currentLineBytes;
    private bool _finished;
    private string? _tempFilePath;

    public OutputAccumulator(IFileSystem fileSystem, OutputAccumulatorOptions? options = null)
    {
        _fileSystem = fileSystem;
        _maxLines = options?.MaxLines ?? Truncation.DefaultMaxLines;
        _maxBytes = options?.MaxBytes ?? Truncation.DefaultMaxBytes;
        _maxRollingBytes = Math.Max(_maxBytes * 2, 1);
        _tempFilePrefix = options?.TempFilePrefix ?? "pi-output";
    }

    public async ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_finished) throw new InvalidOperationException("Cannot append to a finished output accumulator.");
            _totalRawBytes += data.Length;
            AppendDecodedText(Decode(data.Span, flush: false));
            if (_tempFilePath is not null || ShouldUseTempFile())
            {
                await EnsureTempFileAsync(cancellationToken).ConfigureAwait(false);
                await WriteFileResult(_fileSystem.AppendFileAsync(_tempFilePath!, data.ToArray(), cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                _rawChunks.Add(data.ToArray());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_finished) return;
            _finished = true;
            AppendDecodedText(Decode(ReadOnlySpan<byte>.Empty, flush: true));
            if (ShouldUseTempFile()) await EnsureTempFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OutputSnapshot> SnapshotAsync(bool persistIfTruncated = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tail = Truncation.TruncateTail(GetSnapshotText(), new TruncationOptions(_maxLines, _maxBytes));
            var truncated = _totalLines > _maxLines || _totalDecodedBytes > _maxBytes;
            var truncatedBy = truncated ? tail.TruncatedBy ?? (_totalDecodedBytes > _maxBytes ? "bytes" : "lines") : null;
            var truncation = tail with
            {
                Truncated = truncated,
                TruncatedBy = truncatedBy,
                TotalLines = _totalLines,
                TotalBytes = _totalDecodedBytes,
                MaxLines = _maxLines,
                MaxBytes = _maxBytes
            };

            if (persistIfTruncated && truncation.Truncated) await EnsureTempFileAsync(cancellationToken).ConfigureAwait(false);
            return new OutputSnapshot(truncation.Content, truncation, _tempFilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public int GetLastLineBytes() => _currentLineBytes;

    private async Task EnsureTempFileAsync(CancellationToken cancellationToken)
    {
        if (_tempFilePath is not null) return;
        _tempFilePath = await FileResult(_fileSystem.CreateTempFileAsync(_tempFilePrefix, ".log", cancellationToken)).ConfigureAwait(false);
        foreach (var chunk in _rawChunks)
        {
            await WriteFileResult(_fileSystem.AppendFileAsync(_tempFilePath, chunk, cancellationToken)).ConfigureAwait(false);
        }
        _rawChunks.Clear();
    }

    private void AppendDecodedText(string text)
    {
        if (text.Length == 0) return;
        var bytes = Truncation.ByteCount(text);
        _totalDecodedBytes += bytes;
        _tailText += text;
        _tailBytes += bytes;
        if (_tailBytes > _maxRollingBytes * 2) TrimTail();
        var newlines = text.Count(ch => ch == '\n');
        if (newlines == 0)
        {
            _currentLineBytes += bytes;
            return;
        }

        _totalLines += newlines;
        var lastNewline = text.LastIndexOf('\n');
        _currentLineBytes = Truncation.ByteCount(text[(lastNewline + 1)..]);
    }

    private string Decode(ReadOnlySpan<byte> data, bool flush)
    {
        var charCount = _decoder.GetCharCount(data, flush);
        if (charCount == 0) return string.Empty;
        var chars = new char[charCount];
        _decoder.GetChars(data, chars, flush);
        return new string(chars);
    }

    private void TrimTail()
    {
        var buffer = Encoding.UTF8.GetBytes(_tailText);
        if (buffer.Length <= _maxRollingBytes)
        {
            _tailBytes = buffer.Length;
            return;
        }

        var start = buffer.Length - _maxRollingBytes;
        while (start < buffer.Length && (buffer[start] & 0xc0) == 0x80) start++;
        _tailStartsAtLineBoundary = start == 0 ? _tailStartsAtLineBoundary : buffer[start - 1] == 0x0a;
        _tailText = Encoding.UTF8.GetString(buffer[start..]);
        _tailBytes = Truncation.ByteCount(_tailText);
    }

    private string GetSnapshotText()
    {
        if (_tailStartsAtLineBoundary) return _tailText;
        var firstNewline = _tailText.IndexOf('\n');
        return firstNewline == -1 ? _tailText : _tailText[(firstNewline + 1)..];
    }

    private bool ShouldUseTempFile() => _totalRawBytes > _maxBytes || _totalDecodedBytes > _maxBytes || _totalLines > _maxLines;

    private static async Task<string> FileResult(Task<PiSharp.Abstractions.Result<string, FileError>> task)
    {
        var result = await task.ConfigureAwait(false);
        return result.GetOrThrow(error => error);
    }

    private static async Task WriteFileResult(Task<PiSharp.Abstractions.Result<PiSharp.Abstractions.Unit, FileError>> task)
    {
        var result = await task.ConfigureAwait(false);
        result.GetOrThrow(error => error);
    }
}

public sealed record OutputAccumulatorOptions(int? MaxLines = null, int? MaxBytes = null, string? TempFilePrefix = null);

public sealed record OutputSnapshot(string Content, TruncationResult Truncation, string? FullOutputPath);
