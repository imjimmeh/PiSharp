using PiSharp.Abstractions.Options;
using PiSharp.Extensions;

namespace PiSharp.Cli.Parsing;

public enum CliMode { Text, Json, Rpc, SubagentJson }
public enum AppMode { Interactive, PrintText, PrintJson, Rpc, SubagentJson }
public enum CliDiagnosticType { Warning, Error }
public sealed record CliDiagnostic(CliDiagnosticType Type, string Message);

public enum PackageCommandKind { Install, Remove, Update, List, Config }
public sealed record PackageCommandArgs(
    PackageCommandKind Kind,
    string? Source = null,
    bool Local = false,
    bool Force = false,
    bool Self = false,
    bool Extensions = false,
    string? ExtensionSource = null);

public enum DaemonCommandKind { Start, Stop, Status }
public sealed record DaemonCommandArgs(
    DaemonCommandKind Kind,
    string? Port = null,
    bool Foreground = false,
    string? ApiKey = null);

public sealed record CliArgs(
    PackageCommandArgs? PackageCommand = null,
    DaemonCommandArgs? DaemonCommand = null,
    string? Provider = null,
    string? Model = null,
    string? ApiKey = null,
    string? SystemPrompt = null,
    IReadOnlyList<string>? AppendSystemPrompt = null,
    ThinkingLevel? Thinking = null,
    bool Continue = false,
    bool Resume = false,
    string? Attach = null,
    bool Help = false,
    bool Version = false,
    CliMode? Mode = null,
    bool NoSession = false,
    string? Session = null,
    string? Fork = null,
    string? SessionDir = null,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<string>? Tools = null,
    bool NoTools = false,
    bool NoBuiltinTools = false,
    IReadOnlyList<string>? Extensions = null,
    bool NoExtensions = false,
    bool Print = false,
    string? Export = null,
    string? Import = null,
    string? Share = null,
    string? LoginProvider = null,
    bool Logout = false,
    bool Reload = false,
    bool CompatibilityMode = true,
    bool NoSkills = false,
    IReadOnlyList<string>? Skills = null,
    IReadOnlyList<string>? PromptTemplates = null,
    bool NoPromptTemplates = false,
    IReadOnlyList<string>? Themes = null,
    bool NoThemes = false,
    bool NoContextFiles = false,
    bool NoResources = false,
    string? ListModels = null,
    bool ListAllModels = false,
    bool Offline = false,
    bool Verbose = false,
    bool BenchmarkStartup = false,
    bool Local = false,
    IReadOnlyList<string>? Messages = null,
    IReadOnlyList<string>? FileArgs = null,
    IReadOnlyDictionary<string, object?>? UnknownFlags = null,
    IReadOnlyDictionary<string, object?>? ExtensionFlagValues = null,
    bool HelpOnly = false,
    IReadOnlyList<CliDiagnostic>? Diagnostics = null)
{
    public IReadOnlyList<string> MessagesOrEmpty => Messages ?? [];
    public IReadOnlyList<string> FileArgsOrEmpty => FileArgs ?? [];
    public IReadOnlyDictionary<string, object?> UnknownFlagsOrEmpty => UnknownFlags ?? new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> ExtensionFlagValuesOrEmpty => ExtensionFlagValues ?? new Dictionary<string, object?>();
    public IReadOnlyList<CliDiagnostic> DiagnosticsOrEmpty => Diagnostics ?? [];
}

public sealed record CliHelpOptions(IReadOnlyList<ExtensionFlagRegistration> ExtensionFlags);

internal static class CliHelpRenderer
{
    public static string Render(CliHelpOptions? options = null)
    {
        var flags = options?.ExtensionFlags ?? [];
        if (flags.Count == 0) return Text;
        var rendered = string.Join(Environment.NewLine, flags.Select(RenderFlag));
        return Text + Environment.NewLine + Environment.NewLine + "Extension CLI Flags:" + Environment.NewLine + rendered;
    }

    private static string RenderFlag(ExtensionFlagRegistration flag)
        => $"      --{flag.Name}{(flag.Type == ExtensionFlagType.String ? " <value>" : string.Empty),-16} {flag.Description}";

    public const string Text = """
Usage: pisharp [options] [prompt]

Options:
  -h, --help                   Show help.
  -v, --version                Show version.
      --mode <text|json|rpc|subagent-json> Select output mode.
      --provider <name>        Select provider (long-only).
      --model <model>          Select model/provider-model (long-only).
  -p, --print [prompt]         Run print mode.
  -t, --tools <a,b>            Restrict active tools.
      --no-tools, -nt          Disable all tools.
      --no-builtin-tools, -nbt Disable built-in tools.
  -e, --extension <path>       Load extension path.
      --no-resources           Disable resource-loaded extensions, skills, prompt templates, themes, and context files.
      --benchmark-startup      Print startup benchmark timings to stderr.

Package Commands:
  install <source>             Install a package or native .dll extension globally.
  install <source> --local     Install in project settings, or copy .dll to project extensions.
  remove <source>              Remove a package.
  uninstall <source>           Alias for remove.
  update                       Update all packages.
  update pi                    Update Pi itself.
  update self                  Update Pi itself.
  update --self                Update Pi itself.
  update --extensions          Update all extensions.
  update --extension <source>  Update a specific extension.
  update <source>              Update a specific package.
  list                         List installed packages.
  config                       Show package configuration.
""";
}
