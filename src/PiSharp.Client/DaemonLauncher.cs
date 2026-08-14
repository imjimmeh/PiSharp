using System.Diagnostics;

namespace PiSharp.Client;

public sealed class DaemonLauncher(DaemonLeaseStore leaseStore)
{
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task<bool> WaitForHealthyAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = HealthRequestTimeout };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
            }

            await Task.Delay(PollInterval, ct);
        }
        return false;
    }

    public async Task<DaemonLease?> StartDaemonAsync(
        string executable,
        string arguments,
        int port,
        string apiKey,
        string version,
        CancellationToken ct = default,
        IDisposable? startLock = null)
    {
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo)!;
            // Hand the exclusive start lock to the child once it is spawned so it can guard against double-start.
            startLock?.Dispose();
            var healthy = await WaitForHealthyAsync(port, StartTimeout, ct);
            if (!healthy) return null;
            var lease = new DaemonLease(process.Id, port, apiKey, DateTimeOffset.UtcNow, version);
            await leaseStore.WriteAsync(lease, ct);
            return lease;
        }
        finally
        {
            startLock?.Dispose();
        }
    }
}
