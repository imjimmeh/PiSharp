using System.Diagnostics;
using System.Text.Json;
using PiSharp.Server.Serialization;

namespace PiSharp.Client;

public sealed class DaemonLeaseStore(string directory)
{
    public string LeasePath => Path.Combine(directory, "daemon.json");
    public string LockPath => Path.Combine(directory, "daemon.lock");

    public async Task<DaemonLease?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(LeasePath)) return null;
            await using var stream = File.OpenRead(LeasePath);
            var lease = await JsonSerializer.DeserializeAsync<DaemonLease>(stream, ServerJsonSerializer.Options, ct);
            return lease is { } l && ProcessAlive(l.Pid) ? l : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task WriteAsync(DaemonLease lease, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        await using var stream = new FileStream(LeasePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, lease, ServerJsonSerializer.Options, ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(LeasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static bool ProcessAlive(int pid)
    {
        try { return Process.GetProcessById(pid) is { HasExited: false }; }
        catch (ArgumentException) { return false; }
    }
}
