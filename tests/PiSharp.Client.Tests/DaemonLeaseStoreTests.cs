using PiSharp.Client;
using Xunit;

namespace PiSharp.Client.Tests;

public sealed class DaemonLeaseStoreTests
{
    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        using var tempDir = CreateTempDir();
        var store = new DaemonLeaseStore(tempDir.Path);
        var lease = new DaemonLease(Pid: Environment.ProcessId, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0");
        await store.WriteAsync(lease);

        var read = await store.ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(lease.Pid, read.Pid);
        Assert.Equal(lease.Port, read.Port);
        Assert.Equal(lease.ApiKey, read.ApiKey);
        Assert.Equal(lease.StartedAt, read.StartedAt);
        Assert.Equal(lease.Version, read.Version);
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "daemon.json.tmp")));
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsNull()
    {
        using var tempDir = CreateTempDir();
        var store = new DaemonLeaseStore(tempDir.Path);

        var read = await store.ReadAsync();

        Assert.Null(read);
    }

    [Fact]
    public async Task Read_CorruptLeaseFile_ReturnsNull()
    {
        using var tempDir = CreateTempDir();
        var store = new DaemonLeaseStore(tempDir.Path);
        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "daemon.json"), "not-json{{");

        var read = await store.ReadAsync();

        Assert.Null(read);
    }

    [Fact]
    public async Task Read_WithStalePid_ReturnsNull()
    {
        using var tempDir = CreateTempDir();
        var store = new DaemonLeaseStore(tempDir.Path);
        await store.WriteAsync(new DaemonLease(Pid: int.MaxValue, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0"));

        var read = await store.ReadAsync();

        Assert.Null(read);
    }

    private static TempDir CreateTempDir() => TempDir.Create();

    private sealed class TempDir : IDisposable
    {
        private TempDir(string path) => Path = path;

        public string Path { get; }

        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDir(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
