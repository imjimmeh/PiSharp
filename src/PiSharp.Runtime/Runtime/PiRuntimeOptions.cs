using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Options;
using PiSharp.Ai.Auth;
using PiSharp.Runtime.Telemetry;

namespace PiSharp.Runtime;

public sealed record PiRuntimeOptions(
    IExecutionEnv Env,
    string? SessionsRoot = null,
    string? HomeDirectory = null,
    string? Profile = null,
    RuntimeModelOptions? Model = null,
    RuntimeToolOptions? Tools = null,
    RuntimeResourceOptions? Resources = null,
    RuntimePromptOptions? Prompt = null,
    RuntimeExtensionOptions? Extensions = null,
    RuntimeSessionStartupOptions? Session = null,
    HttpClient? HttpClient = null,
    IProviderCredentialResolver? CredentialResolver = null,
    bool CompatibilityMode = true,
    bool BenchmarkStartup = false,
    bool Verbose = false,
    TelemetryService? Telemetry = null);

public sealed record RuntimeModelOptions(
    string? Provider = null,
    string? Model = null,
    ThinkingLevel? Thinking = null,
    IReadOnlyList<string>? ScopedModels = null);

public sealed record RuntimeToolOptions(
    IReadOnlyList<string>? ActiveToolNames = null,
    bool DisableAll = false,
    bool DisableBuiltIns = false);

public sealed record RuntimeResourceOptions(
    IReadOnlyList<string>? ExtensionPaths = null,
    IReadOnlyList<string>? SkillPaths = null,
    IReadOnlyList<string>? PromptTemplatePaths = null,
    IReadOnlyList<string>? ThemePaths = null,
    bool DisableExtensions = false,
    bool DisableTypeScriptExtensions = false,
    bool DisableSkills = false,
    bool DisablePromptTemplates = false,
    bool DisableThemes = false,
    bool DisableContextFiles = false);

public sealed record RuntimePromptOptions(
    string? SystemPrompt = null,
    IReadOnlyList<string>? AppendSystemPrompt = null);

public sealed record RuntimeExtensionOptions(
    IReadOnlyDictionary<string, object?>? FlagValues = null,
    bool DeferCachedActivationUntilUiReady = false);

public sealed record RuntimeSessionStartupOptions(
    bool NoSession = false,
    string? SessionIdOrPath = null,
    bool ContinueLatestForCwd = false,
    string? SessionDirectory = null,
    RuntimeForkStartupOptions? Fork = null,
    string? NewSessionId = null);

public sealed record RuntimeForkStartupOptions(
    string? EntryId = null,
    string? SourceSessionIdOrPath = null,
    string? Position = "before",
    string? NewSessionId = null);
