using PiSharp.Client;
using PiSharp.Server.Hosting;
using Xunit;

namespace PiSharp.Client.Tests;

public sealed class DaemonLauncherTests
{
    [Fact]
    public async Task WaitForHealthyAsync_TimesOut_WhenNoListener()
    {
        using var tempDir = CreateTempDir();
        var launcher = new DaemonLauncher(new DaemonLeaseStore(tempDir.Path));

        var ok = await launcher.WaitForHealthyAsync(port: 1, timeout: TimeSpan.FromMilliseconds(200));

        Assert.False(ok);
    }

    [Fact]
    public async Task WaitForHealthyAsync_ReturnsTrue_WhenServerResponds()
    {
        using var tempDir = CreateTempDir();
        await using var host = new PiServerHost(new PiServerHostOptions { ApiKey = "k" });
        await host.StartAsync(port: 0);
        var launcher = new DaemonLauncher(new DaemonLeaseStore(tempDir.Path));

        var ok = await launcher.WaitForHealthyAsync(host.Port, TimeSpan.FromSeconds(5));

        Assert.True(ok);
    }

    private static TempDir CreateTempDir() => TempDir.Create();

    private sealed class TempDir : IDisposable
    {
        private TempDir(string path) => Path = path;

        public string Path { get; }

        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-launcher-" + Guid.NewGuid().ToString("N"));
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
