using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using PiSharp.Server.Serialization;

namespace PiSharp.Client;

public sealed class DaemonLeaseStore(string directory)
{
    public string LeasePath => Path.Combine(directory, "daemon.json");
    public string LockPath => Path.Combine(directory, "daemon.lock");

    /// <summary>Returns the live lease, or null when no usable lease exists (missing/corrupt file, dead pid, or IO failure).</summary>
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
        var tempPath = LeasePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, lease, ServerJsonSerializer.Options, ct);
            }

            File.Move(tempPath, LeasePath, overwrite: true);
        }
        finally
        {
            DeleteTempFileIfPresent(tempPath);
        }
    }

    private static void DeleteTempFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; never mask the original write failure.
        }
    }

    internal static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
