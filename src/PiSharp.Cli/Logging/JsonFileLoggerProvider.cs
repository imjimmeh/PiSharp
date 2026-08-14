using Microsoft.Extensions.Logging;

namespace PiSharp.Cli.Logging;

/// <summary>
/// <see cref="RollingFileLoggerProvider"/> variant that writes each entry as one JSON-lines object
/// via <see cref="JsonLogFormatter"/> instead of the plain-text timestamped line. Rolling, dated
/// paths, retention and session retargeting behavior are inherited unchanged.
/// </summary>
internal sealed class JsonFileLoggerProvider : RollingFileLoggerProvider
{
    public JsonFileLoggerProvider(RollingFileLoggerOptions options)
        : base(options)
    {
    }

    internal override void Write<TState>(string category, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        if (string.IsNullOrEmpty(formatter(state, exception)) && exception is null) return;

        lock (Gate)
        {
            if (Disposed) return;
            EnsureWriter(DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime));
            Writer!.WriteLine(JsonLogFormatter.Format(category, logLevel, eventId, state, exception, formatter));
            Writer.Flush();
        }
    }
}
