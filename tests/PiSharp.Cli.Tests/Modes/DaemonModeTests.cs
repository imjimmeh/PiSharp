using System.Diagnostics;
using PiSharp.Client;
using PiSharp.Cli.IO;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Parsing;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class DaemonModeTests
{
    [Fact]
    public void StaleLockFile_CanBeAcquiredForNewDaemon()
    {
        using var tempDir = TempDirectory.Create();
        var lockPath = Path.Combine(tempDir.Path, "daemon.lock");
        File.WriteAllText(lockPath, "stale");

        using var daemonLock = DaemonMode.DaemonLock.TryAcquire(lockPath);

        Assert.NotNull(daemonLock);
    }

    [Fact]
    public async Task Start_WhenLockIsHeld_ReportsAlreadyRunning()
    {
        using var tempDir = TempDirectory.Create();
        using var heldLock = DaemonMode.DaemonLock.TryAcquire(Path.Combine(tempDir.Path, "daemon.lock"));
        Assert.NotNull(heldLock);
        var console = new TestConsoleIO();

        var exitCode = await DaemonMode.RunAsync(
            new DaemonCommandArgs(DaemonCommandKind.Start),
            console,
            leaseDirectory: tempDir.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("already running", console.ErrorOutput.ToString());
    }

    [Fact]
    public async Task Stop_WithNoDaemonRunning_ReturnsError()
    {
        using var tempDir = TempDirectory.Create();
        var console = new TestConsoleIO();

        var exitCode = await DaemonMode.RunAsync(
            new DaemonCommandArgs(DaemonCommandKind.Stop),
            console,
            leaseDirectory: tempDir.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("No daemon running", console.ErrorOutput.ToString());
    }

    [Fact]
    public async Task Status_WithStaleLease_ReportsDead()
    {
        using var tempDir = TempDirectory.Create();
        var store = new DaemonLeaseStore(tempDir.Path);
        await store.WriteAsync(new DaemonLease(Pid: int.MaxValue, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0"));
        var console = new TestConsoleIO();

        var exitCode = await DaemonMode.RunAsync(
            new DaemonCommandArgs(DaemonCommandKind.Status),
            console,
            leaseDirectory: tempDir.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("dead", console.Output.ToString());
    }

    [Fact]
    public async Task Status_WithLiveLease_ReportsAlive()
    {
        using var tempDir = TempDirectory.Create();
        var store = new DaemonLeaseStore(tempDir.Path);
        await store.WriteAsync(new DaemonLease(Pid: Process.GetCurrentProcess().Id, Port: 7878, ApiKey: "k", StartedAt: DateTimeOffset.UtcNow, Version: "1.0.0"));
        var console = new TestConsoleIO();

        var exitCode = await DaemonMode.RunAsync(
            new DaemonCommandArgs(DaemonCommandKind.Status),
            console,
            leaseDirectory: tempDir.Path);

        Assert.Equal(0, exitCode);
        Assert.Contains("alive", console.Output.ToString());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-daemon-mode-" + Guid.NewGuid().ToString("N"));

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
