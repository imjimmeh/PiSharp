using System.Text.Json;

namespace PiSharp.Coordination;

public static class CoordinationDaemonConnector
{
    private const int LockRetryDelayMs = 50;
    private const int LockRetryMaxMs = 5000;

    private static readonly JsonSerializerOptions LeaseSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<DaemonConnection> ConnectOrStartAsync(string repo)
    {
        var repoRoot = ResolveRepositoryRoot(repo);
        var metadataDir = Path.Combine(repoRoot, ".pi", "coordination");
        Directory.CreateDirectory(metadataDir);
        var metadataPath = Path.Combine(metadataDir, "daemon.json");

        await using (await AcquireRepoLockAsync(metadataDir))
        {
            var existingLease = TryReadLease(metadataPath);
            if (existingLease is not null && await ProbeAsync(existingLease.PipeName))
            {
                var endpoint = new CoordinationEndpoint(existingLease.PipeName);
                var client = new CoordinationClient(endpoint);
                var updatedLease = existingLease with { LastCheckedAt = DateTimeOffset.UtcNow };
                await WriteLeaseAtomicAsync(metadataPath, updatedLease);
                return new DaemonConnection(endpoint, client, repoRoot, null);
            }

            var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
            var daemon = await CoordinationDaemon.StartAsync(metadataDir, pipeName);
            var newEndpoint = new CoordinationEndpoint(pipeName);
            var newClient = new CoordinationClient(newEndpoint);
            var newLease = new CoordinationDaemonLease(
                Environment.ProcessId, pipeName, repoRoot,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await WriteLeaseAtomicAsync(metadataPath, newLease);
            return new DaemonConnection(newEndpoint, newClient, repoRoot, daemon);
        }
    }

    internal static string ResolveRepositoryRoot(string path)
    {
        var current = Path.GetFullPath(path);
        if (!Directory.Exists(current))
            return current;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var gitPath = Path.Combine(current, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return current;

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;

            current = parent;
        }

        return Path.GetFullPath(path);
    }

    private static async Task<FileStream> AcquireRepoLockAsync(string metadataDir)
    {
        var lockPath = Path.Combine(metadataDir, "daemon.lock");
        var started = Environment.TickCount;

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                if (Environment.TickCount - started > LockRetryMaxMs)
                    throw new TimeoutException("Timed out waiting for coordination daemon repository lock.");

                await Task.Delay(LockRetryDelayMs);
            }
        }
    }

    private static CoordinationDaemonLease? TryReadLease(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CoordinationDaemonLease>(json, LeaseSerializerOptions);
        }
        catch
        {
        }

        return null;
    }

    private static async Task<bool> ProbeAsync(string pipeName)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var client = new CoordinationClient(new CoordinationEndpoint(pipeName));
            await client.GetRosterAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteLeaseAtomicAsync(string path, CoordinationDaemonLease lease)
    {
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(lease, LeaseSerializerOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
