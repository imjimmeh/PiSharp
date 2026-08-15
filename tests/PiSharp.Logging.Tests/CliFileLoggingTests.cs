using Microsoft.Extensions.Logging;
using PiSharp.Logging;
using Xunit;

namespace PiSharp.Logging.Tests;

public sealed class CliFileLoggingTests
{
    [Fact]
    public void GetDefaultLogFilePath_ReturnsCorrectPath()
    {
        var result = CliFileLogging.GetDefaultLogFilePath("/home/user");

        Assert.Equal(Path.Combine("/home/user", ".pi", "PiSharp", "logs", "pi.log"), result);
    }

    [Fact]
    public void GetSessionLogFilePath_MirrorsSessionDirectoryAndFileStem()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var encodedCwd = "--repo-project--";
        var sessionPath = Path.Combine(home, ".pi", "agent", "sessions", encodedCwd, "2026-06-04T10-20-30-000_session-1.jsonl");

        var result = CliFileLogging.GetSessionLogFilePath(Path.Combine(home, ".pi", "PiSharp"), sessionPath);

        Assert.Equal(Path.Combine(home, ".pi", "PiSharp", "logs", encodedCwd, "2026-06-04T10-20-30-000_session-1.log"), result);
    }

    [Fact]
    public void ResolveLogLevel_DefaultsToDebug()
    {
        var result = CliFileLogging.ResolveLogLevel(null, null);

        Assert.Equal(LogLevel.Debug, result);
    }

    [Fact]
    public void ResolveLogLevel_SettingsOverridesDefault()
    {
        var result = CliFileLogging.ResolveLogLevel("Information", null);

        Assert.Equal(LogLevel.Information, result);
    }

    [Fact]
    public void ResolveLogLevel_EnvOverridesSettings()
    {
        var result = CliFileLogging.ResolveLogLevel("Information", "Error");

        Assert.Equal(LogLevel.Error, result);
    }

    [Fact]
    public void ResolveLogLevel_IgnoresInvalidValues()
    {
        var result = CliFileLogging.ResolveLogLevel("NotAValidLevel", null);

        Assert.Equal(LogLevel.Debug, result);
    }

    [Fact]
    public void ResolveLogLevel_NoneIsValid()
    {
        var result = CliFileLogging.ResolveLogLevel("None", null);

        Assert.Equal(LogLevel.None, result);
    }

    [Fact]
    public void ResolveMaxFiles_DefaultsTo7()
    {
        var result = CliFileLogging.ResolveMaxFiles(null, null);

        Assert.Equal(7, result);
    }

    [Fact]
    public void ResolveMaxFiles_SettingsOverridesDefault()
    {
        var result = CliFileLogging.ResolveMaxFiles("3", null);

        Assert.Equal(3, result);
    }

    [Fact]
    public void ResolveMaxFiles_EnvOverridesSettings()
    {
        var result = CliFileLogging.ResolveMaxFiles("3", "5");

        Assert.Equal(5, result);
    }

    [Fact]
    public void ResolveMaxFiles_IgnoresInvalidValues()
    {
        var result = CliFileLogging.ResolveMaxFiles("bad", null);

        Assert.Equal(7, result);
    }

    [Fact]
    public void ResolveLogFilePath_ReturnsDefaultWhenNoOverrides()
    {
        var result = CliFileLogging.ResolveLogFilePath(null, null, "/default/path.log");

        Assert.Equal("/default/path.log", result);
    }

    [Fact]
    public void ResolveLogFilePath_SettingsOverridesDefault()
    {
        var result = CliFileLogging.ResolveLogFilePath("/settings/path.log", null, "/default/path.log");

        Assert.Equal("/settings/path.log", result);
    }

    [Fact]
    public void ResolveLogFilePath_EnvOverridesSettings()
    {
        var result = CliFileLogging.ResolveLogFilePath("/settings/path.log", "/env/path.log", "/default/path.log");

        Assert.Equal("/env/path.log", result);
    }

    [Fact]
    public void ResolveLogFilePath_EmptySettingsDisablesLogging()
    {
        var result = CliFileLogging.ResolveLogFilePath("", null, "/default/path.log");

        Assert.Equal("", result);
    }

    [Fact]
    public void ResolveLogFilePath_EmptyEnvDisablesLogging()
    {
        var result = CliFileLogging.ResolveLogFilePath("/settings/path.log", "", "/default/path.log");

        Assert.Equal("", result);
    }

    [Fact]
    public void ResolveOptions_UsesProjectPiSharpLoggingOverGlobalPiSharpLogging()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-logging-config-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "PiSharp"));
        var globalLog = Path.Combine(root, "global.log");
        var projectLog = Path.Combine(root, "project.log");
        File.WriteAllText(Path.Combine(home, ".pi", "PiSharp", "settings.json"),
            $@"{{ ""logging"": {{ ""file"": ""{globalLog.Replace("\\", "\\\\")}"", ""level"": ""Information"", ""maxFiles"": 3 }} }}");
        File.WriteAllText(Path.Combine(repo, ".pi", "PiSharp", "settings.json"),
            $@"{{ ""logging"": {{ ""file"": ""{projectLog.Replace("\\", "\\\\")}"", ""level"": ""Error"", ""maxFiles"": 5 }} }}");

        var options = CliFileLogging.ResolveOptions(repo, home, overrides: new CliFileLoggingOverrides(null, null, null));

        Assert.NotNull(options);
        Assert.Equal(projectLog, options.FilePath);
        Assert.Equal(LogLevel.Error, options.MinimumLevel);
        Assert.Equal(5, options.MaxRetainedFiles);
    }

    [Fact]
    public void CreateConfiguredFileLogging_DefaultRegistrationRetargetsToSessionLogPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-logging-default-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var encodedCwd = "--repo--";
        var sessionPath = Path.Combine(home, ".pi", "agent", "sessions", encodedCwd, "2026-06-04T10-20-30-000_session-1.jsonl");

        var registration = CliFileLogging.CreateConfiguredFileLogging(repo, home, new CliFileLoggingOverrides(null, null, null));

        Assert.NotNull(registration);
        registration.SetSessionPath(sessionPath);
        Assert.Equal(Path.Combine(home, ".pi", "PiSharp", "logs", encodedCwd, "2026-06-04T10-20-30-000_session-1.log"), registration.CurrentFilePath);
        Assert.Equal(RollingFileMode.ExactFile, registration.Mode);
    }

    [Fact]
    public void CreateConfiguredFileLogging_SettingsFileDoesNotRetarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-logging-explicit-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "PiSharp"));
        var explicitLog = Path.Combine(root, "explicit.log");
        File.WriteAllText(Path.Combine(repo, ".pi", "PiSharp", "settings.json"),
            $@"{{ ""logging"": {{ ""file"": ""{explicitLog.Replace("\\", "\\\\")}"" }} }}");
        var sessionPath = Path.Combine(home, ".pi", "agent", "sessions", "--repo--", "session.jsonl");

        var registration = CliFileLogging.CreateConfiguredFileLogging(repo, home, new CliFileLoggingOverrides(null, null, null));

        Assert.NotNull(registration);
        registration.SetSessionPath(sessionPath);
        Assert.Equal(explicitLog, registration.CurrentFilePath);
        Assert.Equal(RollingFileMode.Dated, registration.Mode);
    }

    [Fact]
    public void CreateConfiguredFileLogging_MemorySessionDoesNotRetarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-logging-memory-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var registration = CliFileLogging.CreateConfiguredFileLogging(repo, home, new CliFileLoggingOverrides(null, null, null));

        Assert.NotNull(registration);
        var originalPath = registration.CurrentFilePath;
        registration.SetSessionPath("memory://session");

        Assert.Equal(originalPath, registration.CurrentFilePath);
    }
}
