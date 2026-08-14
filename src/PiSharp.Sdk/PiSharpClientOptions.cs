namespace PiSharp.Sdk;

/// <summary>
/// Connection options for <see cref="PiSharpClient.ConnectAsync"/>. All values are optional; the
/// client falls back to the daemon lease (written by <c>daemon start</c>) and the local machine's
/// defaults when an option is unset.
/// </summary>
/// <param name="Cwd">
/// Working directory used to resolve the daemon lease location (via
/// <c>PiAgentPaths.FromCwd(Cwd).GlobalPiSharpDirectory</c>). Defaults to the current directory.
/// </param>
/// <param name="LeaseDirectory">
/// Explicit override for the directory that holds the <c>daemon.json</c> lease. When set, it takes
/// precedence over the directory derived from <paramref name="Cwd"/> — useful for tests and custom
/// data roots.
/// </param>
/// <param name="Port">Daemon port override. Default: read from the lease.</param>
/// <param name="ApiKey">API key override. Default: read from the lease.</param>
/// <param name="AutoStartDaemon">
/// When true and no compatible lease exists, spawn a detached daemon via
/// <see cref="PiSharp.Client.DaemonLauncher"/> and write a fresh lease. Default true.
/// </param>
/// <param name="DaemonExecutable">
/// Executable used by <see cref="AutoStartDaemon"/> (spawned with
/// <c>daemon foreground --port &lt;port&gt; --api-key &lt;key&gt;</c>). Defaults to
/// <c>Environment.ProcessPath</c>.
/// </param>
/// <param name="ConnectTimeout">Timeout for the initial connection (WS + /health poll). Default 10s.</param>
public sealed record PiSharpClientOptions(
    string? Cwd = null,
    string? LeaseDirectory = null,
    int? Port = null,
    string? ApiKey = null,
    bool AutoStartDaemon = true,
    string? DaemonExecutable = null,
    TimeSpan ConnectTimeout = default)
{
    /// <summary>Default connection timeout when <see cref="ConnectTimeout"/> is left at its zero value.</summary>
    public static TimeSpan DefaultConnectTimeout { get; } = TimeSpan.FromSeconds(10);

    internal TimeSpan EffectiveConnectTimeout => ConnectTimeout > TimeSpan.Zero ? ConnectTimeout : DefaultConnectTimeout;
}
