using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PiSharp.Cli.Logging;

internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private RollingFileLoggerOptions _options;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);
    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private string? _currentPath;
    private bool _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, category => new RollingFileLogger(category, this));

    internal string FilePath
    {
        get { lock (_gate) return _options.FilePath; }
    }

    internal RollingFileMode Mode
    {
        get { lock (_gate) return _options.Mode; }
    }

    internal void UpdateFilePath(string filePath)
    {
        lock (_gate)
        {
            if (_options.FilePath == filePath) return;
            _writer?.Dispose();
            _writer = null;
            _currentPath = null;
            _options = _options with { FilePath = filePath };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal bool IsEnabled(LogLevel logLevel)
        => !_disposed && logLevel != LogLevel.None && logLevel >= _options.MinimumLevel;

    internal void Write<TState>(string category, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null) return;

        lock (_gate)
        {
            if (_disposed) return;
            EnsureWriter(DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime));

            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            _writer!.Write(timestamp);
            _writer.Write(' ');
            _writer.Write(logLevel.ToString());
            _writer.Write(' ');
            _writer.Write(category);
            if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name))
            {
                _writer.Write(" [");
                _writer.Write(eventId.ToString());
                _writer.Write(']');
            }
            _writer.Write(": ");
            _writer.WriteLine(message);

            if (exception is not null) _writer.WriteLine(exception);
            _writer.Flush();
        }
    }

    private void EnsureWriter(DateOnly date)
    {
        var nextPath = _options.Mode == RollingFileMode.ExactFile ? _options.FilePath : BuildDatedPath(_options.FilePath, date);
        if (_writer is not null && _currentPath == nextPath) return;

        _writer?.Dispose();
        _currentDate = date;
        _currentPath = nextPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_currentPath) ?? ".");
        _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        if (_options.MaxRetainedFiles <= 0 || string.IsNullOrEmpty(_currentPath)) return;

        var directory = Path.GetDirectoryName(_options.FilePath) ?? ".";
        var fileName = Path.GetFileNameWithoutExtension(_options.FilePath);
        var extension = Path.GetExtension(_options.FilePath);
        var searchPattern = _options.Mode == RollingFileMode.ExactFile ? "*.log" : $"{fileName}-????????{extension}";

        foreach (var file in Directory.EnumerateFiles(directory, searchPattern)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(_options.MaxRetainedFiles))
        {
            try { File.Delete(file); }
            catch { /* Logging must not fail the CLI. */ }
        }
    }

    private static string BuildDatedPath(string path, DateOnly date)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var datedName = $"{fileName}-{date:yyyyMMdd}{extension}";
        return string.IsNullOrEmpty(directory) ? datedName : Path.Combine(directory, datedName);
    }

    private sealed class RollingFileLogger(string category, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => provider.Write(category, logLevel, eventId, state, exception, formatter);
    }
}

internal sealed record RollingFileLoggerOptions(string FilePath, LogLevel MinimumLevel, int MaxRetainedFiles, RollingFileMode Mode = RollingFileMode.Dated);

internal enum RollingFileMode
{
    Dated,
    ExactFile
}
