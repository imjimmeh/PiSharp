using System.Diagnostics;
using PiSharp.Client;
using PiSharp.Cli.Modes;
using Xunit;

namespace PiSharp.Cli.Tests;

public sealed class InteractiveModeTests
{
    [Fact]
    public async Task SelectLease_LiveLeaseWins_DoesNotAutoStart()
    {
        using var tempDir = TempDirectory.Create();
        var store = new DaemonLeaseStore(tempDir.Path);
        var stored = new DaemonLease(Pid: Process.GetCurrentProcess().Id, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0");
        await store.WriteAsync(stored);

        var autoStarted = false;
        var lease = await InteractiveMode.SelectLeaseAsync(
            store,
            isHealthy: (_, _) => Task.FromResult(true),
            autoStart: _ =>
            {
                autoStarted = true;
                return Task.FromResult<DaemonLease?>(null);
            });

        Assert.Equal(stored, lease);
        Assert.False(autoStarted);
    }

    [Fact]
    public async Task SelectLease_LiveButUnhealthyLease_AutoStarts()
    {
        using var tempDir = TempDirectory.Create();
        var store = new DaemonLeaseStore(tempDir.Path);
        var stored = new DaemonLease(Pid: Process.GetCurrentProcess().Id, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0");
        await store.WriteAsync(stored);

        var fresh = new DaemonLease(Pid: Process.GetCurrentProcess().Id, Port: 7999, ApiKey: "fresh", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0");
        var lease = await InteractiveMode.SelectLeaseAsync(
            store,
            isHealthy: (_, _) => Task.FromResult(false),
            autoStart: _ => Task.FromResult<DaemonLease?>(fresh));

        Assert.Equal(fresh, lease);
    }

    [Fact]
    public async Task SelectLease_StaleLease_AutoStarts()
    {
        using var tempDir = TempDirectory.Create();
        var store = new DaemonLeaseStore(tempDir.Path);
        await store.WriteAsync(new DaemonLease(Pid: int.MaxValue, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0"));

        var fresh = new DaemonLease(Pid: Process.GetCurrentProcess().Id, Port: 7999, ApiKey: "fresh", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0");
        var lease = await InteractiveMode.SelectLeaseAsync(
            store,
            isHealthy: (_, _) => Task.FromResult(false),
            autoStart: _ => Task.FromResult<DaemonLease?>(fresh));

        Assert.Equal(fresh, lease);
    }

    [Fact]
    public async Task SelectLease_NoLease_AutoStarts()
    {
        using var tempDir = TempDirectory.Create();
        var store = new DaemonLeaseStore(tempDir.Path);

        var fresh = new DaemonLease(Pid: Process.GetCurrentProcess().Id, Port: 7999, ApiKey: "fresh", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0");
        var lease = await InteractiveMode.SelectLeaseAsync(
            store,
            autoStart: _ => Task.FromResult<DaemonLease?>(fresh));

        Assert.Equal(fresh, lease);
    }

    [Fact]
    public async Task SelectLease_AutoStartFails_ReturnsNull()
    {
        using var tempDir = TempDirectory.Create();
        var store = new DaemonLeaseStore(tempDir.Path);

        var lease = await InteractiveMode.SelectLeaseAsync(
            store,
            autoStart: _ => Task.FromResult<DaemonLease?>(null));

        Assert.Null(lease);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-interactive-mode-" + Guid.NewGuid().ToString("N"));

        private TempDirectory() => Directory.CreateDirectory(Path);

        public static TempDirectory Create() => new();

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
