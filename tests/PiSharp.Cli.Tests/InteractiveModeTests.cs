using System.Diagnostics;
using PiSharp.Client;
using PiSharp.Cli.Modes;
using PiSharp.Compatibility.Settings;
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

    [Fact]
    public void WriteThenReadCursorSequence_RoundTrips()
    {
        using var tempDir = TempDirectory.Create();

        InteractiveMode.WriteCursorSequence("sess-1", tempDir.Path, 42, tempDir.Path);

        Assert.Equal(42, InteractiveMode.ReadCursorSequence("sess-1", tempDir.Path, tempDir.Path));
        var cursorPath = CursorFilePath("sess-1", tempDir.Path);
        Assert.True(File.Exists(cursorPath));
        Assert.Equal("42", File.ReadAllText(cursorPath));
    }

    [Fact]
    public void ReadCursorSequence_MissingFile_ReturnsZero()
    {
        using var tempDir = TempDirectory.Create();

        Assert.Equal(0, InteractiveMode.ReadCursorSequence("sess-missing", tempDir.Path, tempDir.Path));
    }

    [Fact]
    public void ReadCursorSequence_GarbageFile_ReturnsZero()
    {
        using var tempDir = TempDirectory.Create();
        var cursorPath = CursorFilePath("sess-garbage", tempDir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        File.WriteAllText(cursorPath, "not-a-number");

        Assert.Equal(0, InteractiveMode.ReadCursorSequence("sess-garbage", tempDir.Path, tempDir.Path));
    }

    [Fact]
    public void ResolveRemoteKeybindingsPath_ReturnsKeybindingsJsonUnderPiAgentPath()
    {
        using var tempDir = TempDirectory.Create();

        var resolved = InteractiveMode.ResolveRemoteKeybindingsPath(tempDir.Path);

        Assert.NotNull(resolved);
        Assert.EndsWith("keybindings.json", resolved, StringComparison.Ordinal);
        Assert.Equal(
            PiAgentPaths.FromCwd(tempDir.Path).GlobalAgentDirectory,
            Path.GetDirectoryName(resolved),
            ignoreCase: true);
    }

    private static string CursorFilePath(string sessionId, string home)
        => Path.Combine(PiAgentPaths.FromCwd(home, home).GlobalPiSharpDirectory, "sessions", $"{sessionId}.cursor");

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
