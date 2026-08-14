using System.Net;

namespace PiSharp.Client;

public static class DaemonDiscovery
{
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(2);

    public static async Task<bool> IsDaemonAvailableAsync(DaemonLease lease, CancellationToken ct = default)
    {
        if (!DaemonLeaseStore.ProcessAlive(lease.Pid)) return false;
        if (!IsRuntimeCompatible(lease.Version)) return false;

        using var client = new HttpClient { Timeout = HealthCheckTimeout };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{lease.Port}/health", ct);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    internal static bool IsRuntimeCompatible(string leaseVersion)
    {
        if (!Version.TryParse(leaseVersion, out var lease)) return false;
        return lease.Major == Environment.Version.Major && lease.Minor == Environment.Version.Minor;
    }
}
