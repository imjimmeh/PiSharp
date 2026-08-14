using PiSharp.Abstractions.Options;

namespace PiSharp.Cli.Parsing;

public static class CliParser
{
    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        var b = new Builder();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? NextValue()
            {
                if (i + 1 >= args.Count)
                {
                    b.Error($"Missing value for {arg}.");
                    return null;
                }
                return args[++i];
            }

            switch (arg)
            {
                case "--help" or "-h": b.Help = true; break;
                case "--version" or "-v": b.Version = true; break;
                case "--mode": b.Mode = ParseMode(NextValue(), b); break;
                case "--continue" or "-c": b.Continue = true; break;
                case "--resume" or "-r": b.Resume = true; break;
                case "--provider": b.Provider = NextValue(); break;
                case "--model": b.Model = NextValue(); break;
                case "--api-key": b.ApiKey = NextValue(); break;
                case "--system-prompt": b.SystemPrompt = NextValue(); break;
                case "--append-system-prompt": b.AppendSystemPrompt.AddIfNotNull(NextValue()); break;
                case "--thinking": b.Thinking = ParseThinking(NextValue(), b); break;
                case "--no-session": b.NoSession = true; break;
                case "--session": b.Session = NextValue(); break;
                case "--fork": b.Fork = NextValue(); break;
                case "--session-dir": b.SessionDir = NextValue(); break;
                case "--models": b.Models.AddRange(SplitCsv(NextValue())); break;
                case "--tools" or "-t": b.Tools.AddRange(SplitCsv(NextValue())); break;
                case "--no-tools" or "-nt": b.NoTools = true; break;
                case "--no-builtin-tools" or "-nbt": b.NoBuiltinTools = true; break;
                case "--extension" or "-e": b.Extensions.AddIfNotNull(NextValue()); break;
                case "--no-extensions" or "-ne": b.NoExtensions = true; break;
                case "--print" or "-p":
                    b.Print = true;
                    if (i + 1 < args.Count)
                    {
                        var next = args[i + 1];
                        if (!next.StartsWith("@", StringComparison.Ordinal) && (!next.StartsWith("-", StringComparison.Ordinal) || next.StartsWith("---", StringComparison.Ordinal))) b.Messages.Add(args[++i]);
                    }
                    break;
                case "--export": b.Export = NextValue(); break;
                case "--import": b.Import = NextValue(); break;
                case "--share": b.Share = NextValue(); break;
                case "--login": b.LoginProvider = i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal) ? args[++i] : "default"; break;
                case "--logout": b.Logout = true; break;
                case "--reload": b.Reload = true; break;
                case "--no-compatibility": b.CompatibilityMode = false; break;
                case "--no-skills" or "-ns": b.NoSkills = true; break;
                case "--skill": b.Skills.AddIfNotNull(NextValue()); break;
                case "--prompt-template": b.PromptTemplates.AddIfNotNull(NextValue()); break;
                case "--no-prompt-templates" or "-np": b.NoPromptTemplates = true; break;
                case "--theme": b.Themes.AddIfNotNull(NextValue()); break;
                case "--no-themes": b.NoThemes = true; break;
                case "--no-context-files" or "-nc": b.NoContextFiles = true; break;
                case "--no-resources": b.NoResources = true; break;
                case "--list-models":
                    if (i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal)) b.ListModels = args[++i];
                    else b.ListAllModels = true;
                    break;
                case "--offline": b.Offline = true; break;
                case "--verbose": b.Verbose = true; break;
                case "--benchmark-startup": b.BenchmarkStartup = true; break;
                case "--local": b.Local = true; break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) CaptureUnknownLong(arg, args, ref i, b);
                    else if (arg.StartsWith("-", StringComparison.Ordinal)) b.Error($"Unknown option: {arg}");
                    else if (arg.StartsWith("@", StringComparison.Ordinal)) b.FileArgs.Add(arg[1..]);
                    else if (TryParsePackageCommand(arg, args, ref i, b)) { /* consumed */ }
                    else if (TryParseDaemonCommand(arg, args, ref i, b)) { /* consumed */ }
                    else b.Messages.Add(arg);
                    break;
            }
        }
        return b.Build();
    }

    private static bool TryParsePackageCommand(string arg, IReadOnlyList<string> args, ref int i, Builder b)
    {
        PackageCommandKind kind;
        switch (arg)
        {
            case "install": kind = PackageCommandKind.Install; break;
            case "remove": case "uninstall": kind = PackageCommandKind.Remove; break;
            case "update": kind = PackageCommandKind.Update; break;
            case "list": kind = PackageCommandKind.List; break;
            case "config": kind = PackageCommandKind.Config; break;
            default: return false;
        }

        if (kind == PackageCommandKind.List)
        {
            b.PackageCommand = new PackageCommandArgs(PackageCommandKind.List);
            return true;
        }

        if (kind == PackageCommandKind.Config)
        {
            b.PackageCommand = new PackageCommandArgs(PackageCommandKind.Config);
            return true;
        }

        if (kind == PackageCommandKind.Install)
        {
            return ParseInstallArgs(args, ref i, b);
        }

        if (kind == PackageCommandKind.Remove)
        {
            return ParseRemoveArgs(args, ref i, b);
        }

        if (kind == PackageCommandKind.Update)
        {
            return ParseUpdateArgs(args, ref i, b);
        }

        return true;
    }

    private static bool TryParseDaemonCommand(string arg, IReadOnlyList<string> args, ref int i, Builder b)
    {
        if (!string.Equals(arg, "daemon", StringComparison.Ordinal)) return false;

        if (i + 1 >= args.Count)
        {
            b.Error("Missing subcommand for daemon.");
            return true;
        }

        DaemonCommandKind kind;
        var foreground = false;
        switch (args[++i])
        {
            case "start": kind = DaemonCommandKind.Start; break;
            case "stop": kind = DaemonCommandKind.Stop; break;
            case "status": kind = DaemonCommandKind.Status; break;
            case "foreground": kind = DaemonCommandKind.Start; foreground = true; break;
            default:
                b.Error($"Unknown daemon subcommand: {args[i]}");
                return true;
        }

        string? port = null;
        string? apiKey = null;
        while (i + 1 < args.Count)
        {
            var next = args[i + 1];
            if (next.StartsWith("@", StringComparison.Ordinal) || !next.StartsWith("-", StringComparison.Ordinal)) break;

            i++;
            switch (next)
            {
                case "--foreground":
                    foreground = true;
                    break;
                case "--port":
                    if (i + 1 >= args.Count)
                    {
                        b.Error("Missing value for --port.");
                        continue;
                    }
                    if (args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        b.Error("Missing value for --port.");
                        continue;
                    }
                    port = args[++i];
                    break;
                case "--api-key":
                    if (i + 1 >= args.Count)
                    {
                        b.Error("Missing value for --api-key.");
                        continue;
                    }
                    if (args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        b.Error("Missing value for --api-key.");
                        continue;
                    }
                    apiKey = args[++i];
                    break;
                default:
                    b.Error($"Unknown option for daemon: {next}");
                    break;
            }
        }

        b.DaemonCommand = new DaemonCommandArgs(kind, port, foreground, apiKey);
        return true;
    }

    private static bool ParseInstallArgs(IReadOnlyList<string> args, ref int i, Builder b)
    {
        if (i + 1 >= args.Count)
        {
            b.Error("Missing package source for install.");
            return true;
        }

        var source = args[++i];
        var local = false;
        var force = false;

        while (i + 1 < args.Count)
        {
            var next = args[i + 1];
            if (next.StartsWith("@", StringComparison.Ordinal)) break;
            if (!next.StartsWith("-", StringComparison.Ordinal)) break;

            i++;
            if (next == "--local")
            {
                local = true;
                continue;
            }

            if (next == "--force")
            {
                force = true;
                continue;
            }

            if (next == "--offline")
            {
                b.Offline = true;
                continue;
            }

            if (next == "--extension")
            {
                b.Error("--extension is not valid for install.");
                continue;
            }

            b.Error($"Unknown option for install: {next}");
        }

        b.PackageCommand = new PackageCommandArgs(PackageCommandKind.Install, source, local, force);
        return true;
    }

    private static bool ParseRemoveArgs(IReadOnlyList<string> args, ref int i, Builder b)
    {
        if (i + 1 >= args.Count)
        {
            b.Error("Missing package source for remove.");
            return true;
        }

        var source = args[++i];
        ConsumeRemainingFlags(args, ref i, b);
        b.PackageCommand = new PackageCommandArgs(PackageCommandKind.Remove, Source: source);
        return true;
    }

    private static bool ParseUpdateArgs(IReadOnlyList<string> args, ref int i, Builder b)
    {
        var source = (string?)null;
        var self = false;
        var extensions = false;
        var extensionSource = (string?)null;
        var force = false;

        // Phase 1: consume positional args (source, pi, self — not flags)
        while (i + 1 < args.Count)
        {
            var next = args[i + 1];
            if (next.StartsWith("@", StringComparison.Ordinal)) break;
            if (next.StartsWith("--", StringComparison.Ordinal)) break;

            if (next == "pi" || next == "self")
            {
                self = true;
                i++;
                continue;
            }

            source = next;
            i++;
            break;
        }

        // Phase 2: consume all remaining flags
        while (i + 1 < args.Count)
        {
            var next = args[i + 1];
            if (!next.StartsWith("--", StringComparison.Ordinal)) break;
            if (next.StartsWith("@", StringComparison.Ordinal)) break;

            i++;
            switch (next)
            {
                case "--self":
                    self = true;
                    break;
                case "--extensions":
                    extensions = true;
                    break;
                case "--extension":
                    if (i + 1 >= args.Count)
                    {
                        b.Error("Missing value for --extension.");
                        continue;
                    }
                    extensionSource = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                case "--offline":
                    b.Offline = true;
                    break;
                default:
                    b.Error($"Unknown option for update: {next}");
                    break;
            }
        }

        if (self && extensions)
        {
            b.Error("Conflicting update targets: pi/self and --extensions.");
        }

        b.PackageCommand = new PackageCommandArgs(PackageCommandKind.Update, source, Self: self, Extensions: extensions, ExtensionSource: extensionSource, Force: force);
        return true;
    }

    private static void ConsumeRemainingFlags(IReadOnlyList<string> args, ref int i, Builder b)
    {
        while (i + 1 < args.Count)
        {
            var next = args[i + 1];
            if (next.StartsWith("@", StringComparison.Ordinal)) break;
            if (!next.StartsWith("-", StringComparison.Ordinal)) break;
            i++;
            if (next.StartsWith("--", StringComparison.Ordinal))
                b.Error($"Unknown option: {next}");
            else
                b.Error($"Unknown option: {next}");
        }
    }

    public static AppMode SelectAppMode(CliArgs args, bool stdinRedirected)
    {
        if (args.Mode == CliMode.Rpc) return AppMode.Rpc;
        if (args.Mode == CliMode.SubagentJson) return AppMode.SubagentJson;
        if (args.Mode == CliMode.Json && args.Print && args.NoSession) return AppMode.SubagentJson;
        if (args.Mode == CliMode.Json) return AppMode.PrintJson;
        if (args.Mode == CliMode.Text) return AppMode.PrintText;
        if (args.Print || stdinRedirected) return AppMode.PrintText;
        return AppMode.Interactive;
    }

    private static CliMode? ParseMode(string? value, Builder b)
    {
        if (value is null) return null;
        return value.ToLowerInvariant() switch
        {
            "text" => CliMode.Text,
            "json" => CliMode.Json,
            "rpc" => CliMode.Rpc,
            "subagent-json" => CliMode.SubagentJson,
            _ => b.ErrorAndReturn<CliMode?>($"Invalid --mode '{value}'.")
        };
    }

    private static ThinkingLevel? ParseThinking(string? value, Builder b)
        => Enum.TryParse<ThinkingLevel>(value, true, out var level) ? level : b.WarningAndReturn<ThinkingLevel?>($"Invalid --thinking '{value}'.");

    private static void CaptureUnknownLong(string arg, IReadOnlyList<string> args, ref int index, Builder b)
    {
        var body = arg[2..];
        var eq = body.IndexOf('=');
        if (eq >= 0)
        {
            b.UnknownFlags[body[..eq]] = body[(eq + 1)..];
            return;
        }

        object? value = true;
        if (index + 1 < args.Count && !args[index + 1].StartsWith("-", StringComparison.Ordinal) && !args[index + 1].StartsWith("@", StringComparison.Ordinal)) value = args[++index];
        b.UnknownFlags[body] = value;
    }

    private static IEnumerable<string> SplitCsv(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private sealed class Builder
    {
        public PackageCommandArgs? PackageCommand;
        public DaemonCommandArgs? DaemonCommand;
        public string? Provider, Model, ApiKey, SystemPrompt, Session, Fork, SessionDir, Export, Import, Share, LoginProvider, ListModels;
        public ThinkingLevel? Thinking;
        public CliMode? Mode;
        public bool Continue, Resume, Help, Version, NoSession, NoTools, NoBuiltinTools, NoExtensions, Print, Logout, Reload, CompatibilityMode = true, NoSkills, NoPromptTemplates, NoThemes, NoContextFiles, NoResources, ListAllModels, Offline, Verbose, BenchmarkStartup, Local;
        public List<string> AppendSystemPrompt { get; } = [];
        public List<string> Models { get; } = [];
        public List<string> Tools { get; } = [];
        public List<string> Extensions { get; } = [];
        public List<string> Skills { get; } = [];
        public List<string> PromptTemplates { get; } = [];
        public List<string> Themes { get; } = [];
        public List<string> Messages { get; } = [];
        public List<string> FileArgs { get; } = [];
        public Dictionary<string, object?> UnknownFlags { get; } = new(StringComparer.Ordinal);
        public List<CliDiagnostic> Diagnostics { get; } = [];
        public void Error(string message) => Diagnostics.Add(new CliDiagnostic(CliDiagnosticType.Error, message));
        public void Warning(string message) => Diagnostics.Add(new CliDiagnostic(CliDiagnosticType.Warning, message));
        public T WarningAndReturn<T>(string message) { Warning(message); return default!; }
        public T ErrorAndReturn<T>(string message) { Error(message); return default!; }

        public CliArgs Build() => new(PackageCommand, DaemonCommand, Provider, Model, ApiKey, SystemPrompt, AppendSystemPrompt, Thinking, Continue, Resume, Help, Version, Mode, NoSession, Session, Fork, SessionDir, Models, Tools, NoTools, NoBuiltinTools, Extensions, NoExtensions, Print, Export, Import, Share, LoginProvider, Logout, Reload, CompatibilityMode, NoSkills, Skills, PromptTemplates, NoPromptTemplates, Themes, NoThemes, NoContextFiles, NoResources, ListModels, ListAllModels, Offline, Verbose, BenchmarkStartup, Local, Messages, FileArgs, UnknownFlags, ExtensionFlagValues: null, HelpOnly: false, Diagnostics);
    }
}

internal static class ListExtensions
{
    public static void AddIfNotNull<T>(this List<T> list, T? value) where T : class
    {
        if (value is not null) list.Add(value);
    }
}
