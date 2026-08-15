using Microsoft.Extensions.Logging;
using PiSharp.Logging;
using Xunit;

namespace PiSharp.Logging.Tests;

public sealed class RollingFileLoggerProviderTests
{
    [Fact]
    public void WritesMessagesToDatedLogFile()
    {
        using var temp = TempDirectory.Create();
        var logPath = Path.Combine(temp.Path, "pi.log");
        var datedPath = Path.Combine(temp.Path, $"pi-{DateTimeOffset.Now:yyyyMMdd}.log");
        using (var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions(logPath, LogLevel.Debug, 7)))
        {
            var logger = provider.CreateLogger("PiSharp.Tests");
            logger.LogDebug("hello {Value}", "world");
        }

        Assert.True(File.Exists(datedPath));
        Assert.Contains("hello world", File.ReadAllText(datedPath));
    }

    [Fact]
    public void SkipsMessagesBelowConfiguredLevel()
    {
        using var temp = TempDirectory.Create();
        var logPath = Path.Combine(temp.Path, "pi.log");
        var datedPath = Path.Combine(temp.Path, $"pi-{DateTimeOffset.Now:yyyyMMdd}.log");
        using (var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions(logPath, LogLevel.Information, 7)))
        {
            var logger = provider.CreateLogger("PiSharp.Tests");
            logger.LogDebug("hidden");
            logger.LogInformation("visible");
        }

        var content = File.ReadAllText(datedPath);
        Assert.DoesNotContain("hidden", content);
        Assert.Contains("visible", content);
    }

    [Fact]
    public void WritesMessagesToExactLogFileWhenConfigured()
    {
        using var temp = TempDirectory.Create();
        var logPath = Path.Combine(temp.Path, "session.log");
        var datedPath = Path.Combine(temp.Path, $"session-{DateTimeOffset.Now:yyyyMMdd}.log");
        using (var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions(logPath, LogLevel.Debug, 7, RollingFileMode.ExactFile)))
        {
            var logger = provider.CreateLogger("PiSharp.Tests");
            logger.LogDebug("session scoped");
        }

        Assert.True(File.Exists(logPath));
        Assert.False(File.Exists(datedPath));
        Assert.Contains("session scoped", File.ReadAllText(logPath));
    }

    [Fact]
    public void PrunesOldDatedFilesToRetentionLimit()
    {
        using var temp = TempDirectory.Create();
        var logPath = Path.Combine(temp.Path, "pi.log");
        for (var i = 1; i <= 4; i++)
        {
            var file = Path.Combine(temp.Path, $"pi-2024010{i}.log");
            File.WriteAllText(file, "old");
            File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }

        using var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions(logPath, LogLevel.Debug, 3));
        provider.CreateLogger("PiSharp.Tests").LogDebug("new");

        var files = Directory.GetFiles(temp.Path, "pi-*.log");
        Assert.Equal(3, files.Length);
        Assert.Contains(files, file => file.EndsWith($"pi-{DateTimeOffset.Now:yyyyMMdd}.log", StringComparison.Ordinal));
    }

    [Fact]
    public void PrunesOldExactLogFilesToRetentionLimit()
    {
        using var temp = TempDirectory.Create();
        var logPath = Path.Combine(temp.Path, "session-new.log");
        for (var i = 1; i <= 4; i++)
        {
            var file = Path.Combine(temp.Path, $"session-old-{i}.log");
            File.WriteAllText(file, "old");
            File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }

        using var provider = new RollingFileLoggerProvider(new RollingFileLoggerOptions(logPath, LogLevel.Debug, 3, RollingFileMode.ExactFile));
        provider.CreateLogger("PiSharp.Tests").LogDebug("new");

        var files = Directory.GetFiles(temp.Path, "*.log");
        Assert.Equal(3, files.Length);
        Assert.Contains(files, file => file.EndsWith("session-new.log", StringComparison.Ordinal));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-logging-" + Guid.NewGuid().ToString("N"));

        private TempDirectory() => Directory.CreateDirectory(Path);

        public static TempDirectory Create() => new();

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
