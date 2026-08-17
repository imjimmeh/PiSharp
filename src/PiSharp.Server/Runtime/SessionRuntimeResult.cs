using Microsoft.Extensions.Logging;

namespace PiSharp.Server.Runtime;

/// <summary>
/// Outcome of creating a daemon session runtime: the runtime itself plus, when the host configured
/// per-session file logging, the session-scoped <see cref="ILoggerFactory"/> whose lifetime is owned
/// by the <see cref="LiveServerSession"/> (disposed with it). <c>null</c> means the session shares
/// the daemon-wide factory without owning it.
/// </summary>
public sealed record SessionRuntimeResult(PiSharp.Runtime.SessionRuntime Runtime, ILoggerFactory? LoggerFactory);
