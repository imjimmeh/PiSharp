using Microsoft.Extensions.Logging;
using PiSharp.Compatibility.Settings;

namespace PiSharp.Logging;

public enum LogContext
{
    Client,
    Daemon
}

public static class CliFileLogging
{
    public const string PathEnvironmentVariable = "PISHARP_LOG_FILE";
    public const string LevelEnvironmentVariable = "PISHARP_LOG_LEVEL";
    public const string MaxFilesEnvironmentVariable = "PISHARP_LOG_MAX_FILES";
    public const string FormatEnvironmentVariable = "PISHARP_LOG_FORMAT";

    public static CliFileLoggingRegistration? AddConfiguredFileLogging(ILoggingBuilder builder, string? cwd = null, string? profile = null, LogContext context = LogContext.Client)
    {
        var registration = CreateConfiguredFileLogging(cwd ?? Directory.GetCurrentDirectory(), profile: profile, context: context);
        if (registration is null) return null;

        builder.AddProvider(registration.Provider);
        return registration;
    }

    public static RollingFileLoggerOptions? ResolveOptions(string cwd, string? homeDirectory = null, string? profile = null, CliFileLoggingOverrides? overrides = null, LogContext context = LogContext.Client)
        => ResolveConfiguration(cwd, homeDirectory, overrides, profile, context)?.Options;

    public static CliFileLoggingRegistration? CreateConfiguredFileLogging(string cwd, string? homeDirectory = null, CliFileLoggingOverrides? overrides = null, string? profile = null, LogContext context = LogContext.Client)
    {
        var configuration = ResolveConfiguration(cwd, homeDirectory, overrides, profile, context);
        if (configuration is null) return null;

        var provider = configuration.Options.Json
            ? new JsonFileLoggerProvider(configuration.Options)
            : new RollingFileLoggerProvider(configuration.Options);
        return new CliFileLoggingRegistration(provider, configuration.HomeDirectory, configuration.RetargetsToSession, configuration.GlobalPiSharpDirectory, context);
    }

    private static ResolvedFileLogging? ResolveConfiguration(string cwd, string? homeDirectory, CliFileLoggingOverrides? overrides, string? profile = null, LogContext context = LogContext.Client)
    {
        var paths = PiAgentPaths.FromCwd(cwd, homeDirectory, profile);
        var configuration = PiSettingsConfiguration.Build(paths);
        var logging = PiLoggingSettings.FromConfiguration(configuration);
        overrides ??= CliFileLoggingOverrides.FromEnvironment();

        var defaultPath = GetDefaultLogFilePath(context, paths.GlobalPiSharpDirectory);
        var resolvedPath = ResolveLogFilePath(logging.File, overrides.File, defaultPath);
        if (string.IsNullOrWhiteSpace(resolvedPath)) return null;

        var resolvedLevel = ResolveLogLevel(logging.Level, overrides.Level);
        var resolvedMaxFiles = ResolveMaxFiles(logging.MaxFiles?.ToString(), overrides.MaxFiles);
        var jsonFormat = ResolveJsonFormat(logging.Json, overrides.Format);
        var retargetsToSession = logging.File is null && overrides.File is null;
        var mode = retargetsToSession ? RollingFileMode.ExactFile : RollingFileMode.Dated;
        return new ResolvedFileLogging(new RollingFileLoggerOptions(resolvedPath, resolvedLevel, resolvedMaxFiles, mode, jsonFormat), paths.HomeDirectory, retargetsToSession, paths.GlobalPiSharpDirectory);
    }

    public static string GetDefaultLogFilePath(LogContext context = LogContext.Client, string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pi", "PiSharp", "logs", context.ToString().ToLowerInvariant(), "pi.log");
    }

    public static string GetSessionLogFilePath(string globalPiSharpDirectory, LogContext context, string sessionPath)
    {
        var encodedCwd = Path.GetFileName(Path.GetDirectoryName(sessionPath)) ?? string.Empty;
        var sessionStem = Path.GetFileNameWithoutExtension(sessionPath);
        return Path.Combine(globalPiSharpDirectory, "logs", context.ToString().ToLowerInvariant(), encodedCwd, sessionStem + ".log");
    }
    public static string ResolveLogFilePath(string? settingsFile, string? envFile, string defaultPath)
    {
        if (envFile is not null) return envFile;
        if (settingsFile is not null) return settingsFile;
        return defaultPath;
    }

    public static LogLevel ResolveLogLevel(string? settingsLevel, string? envLevel)
    {
        var resolved = ParseLogLevel(settingsLevel) ?? LogLevel.Debug;
        resolved = ParseLogLevel(envLevel) ?? resolved;
        return resolved;
    }

    public static int ResolveMaxFiles(string? settingsMaxFiles, string? envMaxFiles)
    {
        var resolved = ParsePositiveInt(settingsMaxFiles) ?? 7;
        resolved = ParsePositiveInt(envMaxFiles) ?? resolved;
        return resolved;
    }

    public static bool ResolveJsonFormat(bool settingsJson, string? envFormat)
    {
        if (envFormat is not null) return string.Equals(envFormat, "json", StringComparison.OrdinalIgnoreCase);
        return settingsJson;
    }

    private static LogLevel? ParseLogLevel(string? value)
        => Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : null;

    private static int? ParsePositiveInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}

public sealed class CliFileLoggingRegistration(RollingFileLoggerProvider provider, string homeDirectory, bool retargetsToSession, string globalPiSharpDirectory, LogContext context = LogContext.Client)
{
    public RollingFileLoggerProvider Provider { get; } = provider;
    public string CurrentFilePath => Provider.FilePath;
    public RollingFileMode Mode => Provider.Mode;
    public LogContext Context { get; } = context;

    public void SetSessionPath(string sessionPath)
    {
        if (!retargetsToSession) return;
        if (sessionPath.StartsWith("memory://", StringComparison.OrdinalIgnoreCase)) return;
        Provider.UpdateFilePath(CliFileLogging.GetSessionLogFilePath(globalPiSharpDirectory, context, sessionPath));
    }

    /// <summary>
    /// Retargets to <c>logs/&lt;context&gt;/&lt;encodedCwd&gt;/pi.log</c> so logs emitted before the
    /// session path is known (e.g. runtime bootstrap) still land under the working folder the app
    /// ran in. The later <see cref="SetSessionPath"/> call moves to the session file in the same folder.
    /// </summary>
    public void SetLogFolderPath(string cwd)
    {
        if (!retargetsToSession) return;
        var encodedCwd = PiAgentPaths.EncodeCwd(cwd);
        Provider.UpdateFilePath(Path.Combine(globalPiSharpDirectory, "logs", context.ToString().ToLowerInvariant(), encodedCwd, "pi.log"));
    }
}

public sealed record ResolvedFileLogging(RollingFileLoggerOptions Options, string HomeDirectory, bool RetargetsToSession, string GlobalPiSharpDirectory);

public sealed record CliFileLoggingOverrides(string? File, string? Level, string? MaxFiles, string? Format = null)
{
    public static CliFileLoggingOverrides FromEnvironment()
        => new(
            Environment.GetEnvironmentVariable(CliFileLogging.PathEnvironmentVariable),
            Environment.GetEnvironmentVariable(CliFileLogging.LevelEnvironmentVariable),
            Environment.GetEnvironmentVariable(CliFileLogging.MaxFilesEnvironmentVariable),
            Environment.GetEnvironmentVariable(CliFileLogging.FormatEnvironmentVariable));
}
