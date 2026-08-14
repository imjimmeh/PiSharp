using Microsoft.Extensions.Logging;
using PiSharp.Compatibility.Settings;

namespace PiSharp.Cli.Logging;

internal static class CliFileLogging
{
    public const string PathEnvironmentVariable = "PISHARP_LOG_FILE";
    public const string LevelEnvironmentVariable = "PISHARP_LOG_LEVEL";
    public const string MaxFilesEnvironmentVariable = "PISHARP_LOG_MAX_FILES";
    public const string FormatEnvironmentVariable = "PISHARP_LOG_FORMAT";

    public static CliFileLoggingRegistration? AddConfiguredFileLogging(ILoggingBuilder builder, string? cwd = null, string? profile = null)
    {
        var registration = CreateConfiguredFileLogging(cwd ?? Directory.GetCurrentDirectory(), profile: profile);
        if (registration is null) return null;

        builder.AddProvider(registration.Provider);
        return registration;
    }

    internal static RollingFileLoggerOptions? ResolveOptions(string cwd, string? homeDirectory = null, string? profile = null, CliFileLoggingOverrides? overrides = null)
        => ResolveConfiguration(cwd, homeDirectory, overrides, profile)?.Options;

    internal static CliFileLoggingRegistration? CreateConfiguredFileLogging(string cwd, string? homeDirectory = null, CliFileLoggingOverrides? overrides = null, string? profile = null)
    {
        var configuration = ResolveConfiguration(cwd, homeDirectory, overrides, profile);
        if (configuration is null) return null;

        var provider = configuration.Options.Json
            ? new JsonFileLoggerProvider(configuration.Options)
            : new RollingFileLoggerProvider(configuration.Options);
        return new CliFileLoggingRegistration(provider, configuration.HomeDirectory, configuration.RetargetsToSession, configuration.GlobalPiSharpDirectory);
    }

    private static ResolvedFileLogging? ResolveConfiguration(string cwd, string? homeDirectory, CliFileLoggingOverrides? overrides, string? profile = null)
    {
        var paths = PiAgentPaths.FromCwd(cwd, homeDirectory, profile);
        var configuration = PiSettingsConfiguration.Build(paths);
        var logging = PiLoggingSettings.FromConfiguration(configuration);
        overrides ??= CliFileLoggingOverrides.FromEnvironment();

        var defaultPath = Path.Combine(paths.GlobalPiSharpDirectory, "logs", "pi.log");
        var resolvedPath = ResolveLogFilePath(logging.File, overrides.File, defaultPath);
        if (string.IsNullOrWhiteSpace(resolvedPath)) return null;

        var resolvedLevel = ResolveLogLevel(logging.Level, overrides.Level);
        var resolvedMaxFiles = ResolveMaxFiles(logging.MaxFiles?.ToString(), overrides.MaxFiles);
        var jsonFormat = ResolveJsonFormat(logging.Json, overrides.Format);
        var retargetsToSession = logging.File is null && overrides.File is null;
        var mode = retargetsToSession ? RollingFileMode.ExactFile : RollingFileMode.Dated;
        return new ResolvedFileLogging(new RollingFileLoggerOptions(resolvedPath, resolvedLevel, resolvedMaxFiles, mode, jsonFormat), paths.HomeDirectory, retargetsToSession, paths.GlobalPiSharpDirectory);
    }

    internal static string GetDefaultLogFilePath(string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pi", "PiSharp", "logs", "pi.log");
    }

    internal static string GetSessionLogFilePath(string globalPiSharpDirectory, string sessionPath)
    {
        var encodedCwd = Path.GetFileName(Path.GetDirectoryName(sessionPath)) ?? string.Empty;
        var sessionStem = Path.GetFileNameWithoutExtension(sessionPath);
        return Path.Combine(globalPiSharpDirectory, "logs", encodedCwd, sessionStem + ".log");
    }

    internal static string ResolveLogFilePath(string? settingsFile, string? envFile, string defaultPath)
    {
        if (envFile is not null) return envFile;
        if (settingsFile is not null) return settingsFile;
        return defaultPath;
    }

    internal static LogLevel ResolveLogLevel(string? settingsLevel, string? envLevel)
    {
        var resolved = ParseLogLevel(settingsLevel) ?? LogLevel.Debug;
        resolved = ParseLogLevel(envLevel) ?? resolved;
        return resolved;
    }

    internal static int ResolveMaxFiles(string? settingsMaxFiles, string? envMaxFiles)
    {
        var resolved = ParsePositiveInt(settingsMaxFiles) ?? 7;
        resolved = ParsePositiveInt(envMaxFiles) ?? resolved;
        return resolved;
    }

    internal static bool ResolveJsonFormat(bool settingsJson, string? envFormat)
    {
        if (envFormat is not null) return string.Equals(envFormat, "json", StringComparison.OrdinalIgnoreCase);
        return settingsJson;
    }

    private static LogLevel? ParseLogLevel(string? value)
        => Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : null;

    private static int? ParsePositiveInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}

internal sealed class CliFileLoggingRegistration(RollingFileLoggerProvider provider, string homeDirectory, bool retargetsToSession, string globalPiSharpDirectory)
{
    public RollingFileLoggerProvider Provider { get; } = provider;
    public string CurrentFilePath => Provider.FilePath;
    public RollingFileMode Mode => Provider.Mode;

    public void SetSessionPath(string sessionPath)
    {
        if (!retargetsToSession) return;
        if (sessionPath.StartsWith("memory://", StringComparison.OrdinalIgnoreCase)) return;
        Provider.UpdateFilePath(CliFileLogging.GetSessionLogFilePath(globalPiSharpDirectory, sessionPath));
    }
}

internal sealed record ResolvedFileLogging(RollingFileLoggerOptions Options, string HomeDirectory, bool RetargetsToSession, string GlobalPiSharpDirectory);

internal sealed record CliFileLoggingOverrides(string? File, string? Level, string? MaxFiles, string? Format = null)
{
    public static CliFileLoggingOverrides FromEnvironment()
        => new(
            Environment.GetEnvironmentVariable(CliFileLogging.PathEnvironmentVariable),
            Environment.GetEnvironmentVariable(CliFileLogging.LevelEnvironmentVariable),
            Environment.GetEnvironmentVariable(CliFileLogging.MaxFilesEnvironmentVariable),
            Environment.GetEnvironmentVariable(CliFileLogging.FormatEnvironmentVariable));
}
