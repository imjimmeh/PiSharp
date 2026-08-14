using PiSharp.Cli.IO;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Parsing;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class DaemonModeTests
{
    [Fact]
    public async Task Start_WhenLockFileExists_ReportsAlreadyRunning()
    {
        using var tempDir = TempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "daemon.lock"), "locked");
        var console = new TestConsoleIO();

        var exitCode = await DaemonMode.RunAsync(
            new DaemonCommandArgs(DaemonCommandKind.Start),
            console,
            leaseDirectory: tempDir.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("already running", console.ErrorOutput.ToString());
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
