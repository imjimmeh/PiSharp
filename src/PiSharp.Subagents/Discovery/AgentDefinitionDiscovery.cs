using PiSharp.Subagents.AgentDefinitions;
namespace PiSharp.Subagents.Discovery;

/// <summary>
/// Tiered agent-definition discovery with first-wins precedence:
/// Project (0) &gt; User (1) &gt; Extension (2) &gt; Bundled (3). Duplicate names never produce two
/// entries — the highest-priority definition wins and lower tiers are dropped for that name.
/// </summary>
public sealed class AgentDefinitionDiscovery
{
    private readonly IReadOnlyList<string> _projectDirs;
    private readonly IReadOnlyList<string> _userDirs;
    private readonly IReadOnlyList<string> _extensionDirs;

    public AgentDefinitionDiscovery(
        IReadOnlyList<string>? projectDirs = null,
        IReadOnlyList<string>? userDirs = null,
        IReadOnlyList<string>? extensionDirs = null)
    {
        _projectDirs = projectDirs ?? [];
        _userDirs = userDirs ?? [];
        _extensionDirs = extensionDirs ?? [];
    }

    public static AgentDefinitionDiscovery FromCwd(string cwd, string? homeDirectory = null)
    {
        var projectDirs = new List<string> { Path.Combine(cwd, ".pi", "agents") };
        var userDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(homeDirectory))
            userDirs.Add(Path.Combine(homeDirectory, ".pi", "agent", "agents"));
        return new AgentDefinitionDiscovery(projectDirs, userDirs);
    }

    /// <summary>Returns the dedup'd name → definition map with first-wins precedence applied.</summary>
    public IReadOnlyDictionary<string, AgentDefinition> Discover()
    {
        var result = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
        var diagnostics = new List<AgentDiagnostic>();

        CollectTier(_projectDirs, AgentSourceKind.Project, result, diagnostics);
        CollectTier(_userDirs, AgentSourceKind.User, result, diagnostics);
        CollectTier(_extensionDirs, AgentSourceKind.Extension, result, diagnostics);
        foreach (var bundled in BundledAgents.All)
            result.TryAdd(bundled.Key, bundled.Value);

        return result;
    }

    private static void CollectTier(
        IReadOnlyList<string> directories,
        AgentSourceKind source,
        Dictionary<string, AgentDefinition> result,
        List<AgentDiagnostic> diagnostics)
    {
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
            {
                AgentDefinitionParseResult parsed;
                try
                {
                    parsed = AgentDefinitionParser.Parse(File.ReadAllText(file), file, source);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new("error", "read_failed", exception.Message, file));
                    continue;
                }

                if (parsed.Definition is null)
                {
                    if (parsed.Diagnostic is not null)
                        diagnostics.Add(parsed.Diagnostic);
                    continue;
                }

                // First-wins: an existing entry (from a higher-priority tier or earlier directory)
                // keeps its slot.
                if (!result.ContainsKey(parsed.Definition.Name))
                    result[parsed.Definition.Name] = parsed.Definition;
            }
        }
    }
}
