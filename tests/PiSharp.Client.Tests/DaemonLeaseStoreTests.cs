using PiSharp.Client;
using Xunit;

namespace PiSharp.Client.Tests;

public sealed class DaemonLeaseStoreTests
{
    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var dir = CreateTempDir();
        var store = new DaemonLeaseStore(dir);
        var lease = new DaemonLease(Pid: Environment.ProcessId, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0");
        await store.WriteAsync(lease);

        var read = await store.ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(lease.Port, read.Port);
        Assert.Equal(lease.ApiKey, read.ApiKey);
    }

    [Fact]
    public async Task Read_WithStalePid_ReturnsNull()
    {
        var dir = CreateTempDir();
        var store = new DaemonLeaseStore(dir);
        await store.WriteAsync(new DaemonLease(Pid: int.MaxValue, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0"));

        var read = await store.ReadAsync();

        Assert.Null(read);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
