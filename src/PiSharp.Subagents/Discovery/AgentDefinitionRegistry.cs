using PiSharp.Subagents.AgentDefinitions;

namespace PiSharp.Subagents.Discovery;

/// <summary>
/// Live, refreshable registry of agent definitions backing the <c>/agents</c> command and
/// <c>get_agents</c>. Case-sensitive exact lookups; hides disabled and <c>hide</c>-flagged entries
/// from listings while keeping them spawnable by explicit name.
/// </summary>
public sealed class AgentDefinitionRegistry
{
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, AgentDefinition> _all = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
    private IReadOnlySet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>All resolved agent definitions keyed by exact case-sensitive name.</summary>
    public IReadOnlyDictionary<string, AgentDefinition> All
    {
        get { lock (_gate) { return _all; } }
    }

    public AgentDefinition? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate) { return _all.TryGetValue(name, out var definition) ? definition : null; }
    }

    public bool IsDisabled(string name)
    {
        lock (_gate) { return _disabled.Contains(name); }
    }

    /// <summary>Definitions visible to the <c>/agents</c> listing: not hidden and not disabled.</summary>
    public IReadOnlyCollection<AgentDefinition> ListVisible()
    {
        lock (_gate)
        {
            return _all.Values
                .Where(definition => !definition.Hide && !_disabled.Contains(definition.Name))
                .OrderBy(definition => definition.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>Replaces the full registry contents (discovery refresh, extension reload).</summary>
    public void Replace(IReadOnlyDictionary<string, AgentDefinition> definitions, IReadOnlySet<string>? disabledAgents = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        lock (_gate)
        {
            _all = definitions;
            _disabled = disabledAgents ?? new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
