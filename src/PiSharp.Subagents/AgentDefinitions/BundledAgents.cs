using System.Reflection;

namespace PiSharp.Subagents.AgentDefinitions;

/// <summary>
/// Loads the plugin's embedded bundled agent definitions (omp-parity set: task, scout, sonic,
/// designer, reviewer, security-reviewer, librarian).
/// </summary>
public static class BundledAgents
{
    public static IReadOnlyList<string> Names { get; } =
    [
        "task",
        "scout",
        "sonic",
        "designer",
        "reviewer",
        "security-reviewer",
        "librarian",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, AgentDefinition>> Loaded = new(LoadAll);

    /// <summary>All bundled agent definitions keyed by name (dedup'd; bundled is the lowest tier).</summary>
    public static IReadOnlyDictionary<string, AgentDefinition> All => Loaded.Value;

    private static IReadOnlyDictionary<string, AgentDefinition> LoadAll()
    {
        var assembly = typeof(BundledAgents).Assembly;
        var resources = assembly.GetManifestResourceNames();
        var result = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            var resourceName = resources.FirstOrDefault(candidate =>
                candidate.EndsWith($".Bundled.{name}.md", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            var parsed = AgentDefinitionParser.Parse(content, $"embedded://bundled/{name}.md", AgentSourceKind.Bundled);
            if (parsed.Definition is not null)
                result[parsed.Definition.Name] = parsed.Definition;
        }
        return result;
    }
}
