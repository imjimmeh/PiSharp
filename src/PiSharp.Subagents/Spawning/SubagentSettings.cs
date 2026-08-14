namespace PiSharp.Subagents.Spawning;

/// <summary>
/// Subagent settings mirroring the <c>subagents.*</c> settings-store keys (plan §9). Consumed from
/// environment variables as a fallback until the <c>IExtensionApi.Settings</c> member lands (P02);
/// the settings-store integration point is marked below.
/// </summary>
public sealed record SubagentSettings
{
    /// <summary>Additional discovery roots (in addition to project/user defaults).</summary>
    public IReadOnlyList<string> AgentsDir { get; init; } = [];

    /// <summary>Agent names never spawnable (policy rule 1).</summary>
    public IReadOnlySet<string> DisabledAgents { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Depth cap shared with the <c>task</c> tool (omp default).</summary>
    public int MaxRecursionDepth { get; init; } = 2;

    /// <summary>Global additive skill set for all subagents.</summary>
    public IReadOnlyList<string> AutoloadSkills { get; init; } = [];

    /// <summary>Fallback active-tool set when an agent has no explicit <c>tools</c>.</summary>
    public IReadOnlyList<string> DefaultTools { get; init; } = [];

    /// <summary><c>off</c> | <c>directory-copy</c> | <c>git-worktree</c>.</summary>
    public string IsolationBackend { get; init; } = "off";

    /// <summary>Builds settings from <c>PISHARP_SUBAGENTS_*</c> environment variables.
    /// TODO(settings): replace with PiSettingsStore <c>subagents.*</c> reads once IExtensionApi.Settings
    /// lands (P02) — keys: agentsDir, disabledAgents, maxRecursionDepth, autoloadSkills, defaultTools,
    /// isolationBackend.</summary>
    public static SubagentSettings FromEnvironment()
    {
        var agentsDir = Split(Environment.GetEnvironmentVariable("PISHARP_SUBAGENTS_AGENTS_DIR"), ';');
        var disabled = Split(Environment.GetEnvironmentVariable("PISHARP_SUBAGENTS_DISABLED_AGENTS"), ',');
        var autoload = Split(Environment.GetEnvironmentVariable("PISHARP_SUBAGENTS_AUTOLOAD_SKILLS"), ',');
        var defaultTools = Split(Environment.GetEnvironmentVariable("PISHARP_SUBAGENTS_DEFAULT_TOOLS"), ',');
        var depth = int.TryParse(Environment.GetEnvironmentVariable("PISHARP_SUBAGENTS_MAX_RECURSION_DEPTH"), out var parsed)
            && parsed > 0
                ? parsed
                : 2;
        var isolation = Environment.GetEnvironmentVariable("PISHARP_SUBAGENTS_ISOLATION_BACKEND") ?? "off";

        return new SubagentSettings
        {
            AgentsDir = agentsDir,
            DisabledAgents = new HashSet<string>(disabled, StringComparer.Ordinal),
            MaxRecursionDepth = depth,
            AutoloadSkills = autoload,
            DefaultTools = defaultTools,
            IsolationBackend = isolation,
        };
    }

    public static SubagentSettings Default { get; } = new();

    private static IReadOnlyList<string> Split(string? value, char separator)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
