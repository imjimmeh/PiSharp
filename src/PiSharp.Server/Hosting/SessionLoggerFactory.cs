using Microsoft.Extensions.Logging;

namespace PiSharp.Server.Hosting;

/// <summary>
/// <see cref="ILoggerFactory"/> used for a daemon session's runtime. Every record is forwarded to
/// the daemon-wide host factory (preserving the existing contract that host-supplied loggers receive
/// session diagnostics) and, in parallel, to an optional secondary factory that owns a session-scoped
/// file provider writing to <c>logs/daemon/&lt;cwd&gt;/&lt;session&gt;.log</c>. Disposal releases only
/// the secondary (session) resources — the shared primary factory remains owned by the daemon host.
/// </summary>
internal sealed class SessionLoggerFactory(ILoggerFactory primary, ILoggerFactory? secondary) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName)
        => new FanOutLogger(primary.CreateLogger(categoryName), secondary is null ? null : secondary.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => primary.AddProvider(provider);

    public void Dispose() => secondary?.Dispose();

    private sealed class FanOutLogger(ILogger? primary, ILogger? secondary) : ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (primary is not null && primary.IsEnabled(logLevel)) primary.Log(logLevel, eventId, state, exception, formatter);
            if (secondary is not null && secondary.IsEnabled(logLevel)) secondary.Log(logLevel, eventId, state, exception, formatter);
        }

        public bool IsEnabled(LogLevel logLevel)
            => (primary?.IsEnabled(logLevel) ?? false) || (secondary?.IsEnabled(logLevel) ?? false);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => primary?.BeginScope(state);
    }
}
