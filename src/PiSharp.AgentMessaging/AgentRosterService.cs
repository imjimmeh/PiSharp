using System.Collections.Concurrent;

namespace PiSharp.AgentMessaging;

/// <summary>
/// In-process agent roster: the set of addressable agents (keyed by session id)
/// with their lifecycle status and family relationships (parent / siblings /
/// children). Populated by the extension from session lifecycle and subagent
/// events, and directly via <see cref="Register"/> for tests and hosts.
/// Thread-safe; every mutation publishes a full-family <see cref="Changed"/> event.
/// </summary>
public sealed class AgentRosterService
{
    private readonly ConcurrentDictionary<string, AgentInfo> _agents = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private List<Action<AgentRoster>>? _changeHandlers;

    /// <summary>
    /// Raised (on a background thread) after every roster mutation with a full
    /// roster snapshot — the extension forwards this to the daemon wire as
    /// <c>agent_roster_update</c>.
    /// </summary>
    public event Action<AgentRoster>? Changed
    {
        add { lock (_gate) { _changeHandlers ??= []; _changeHandlers.Add(value!); } }
        remove { lock (_gate) { _changeHandlers?.Remove(value!); } }
    }

    public int Count => _agents.Count;

    /// <summary>Registers or replaces a roster entry and publishes the change.</summary>
    public void Register(AgentInfo agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agents[agent.AgentId] = agent;
        FireChanged();
    }

    /// <summary>Updates an entry's status (and optionally last-active time); no-op when unknown.</summary>
    public bool UpdateStatus(string agentId, AgentStatus status, DateTimeOffset? lastActiveAt = null)
    {
        if (!_agents.TryGetValue(agentId, out var existing))
            return false;

        _agents[agentId] = existing with
        {
            Status = status,
            LastActiveAt = lastActiveAt ?? existing.LastActiveAt,
        };
        FireChanged();
        return true;
    }

    /// <summary>Removes an entry (dispose / gone) and publishes the change.</summary>
    public bool Remove(string agentId)
    {
        var removed = _agents.TryRemove(agentId, out _);
        if (removed) FireChanged();
        return removed;
    }

    /// <summary>Snapshot of the whole roster (all families).</summary>
    public IReadOnlyList<AgentInfo> GetRoster()
        => _agents.Values.OrderBy(a => a.CreatedAt).ThenBy(a => a.AgentId, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Resolves the parent agent id of <paramref name="agentId"/> from the
    /// roster, or null when the agent is unknown or is a root.
    /// </summary>
    public string? ResolveParent(string agentId)
        => _agents.TryGetValue(agentId, out var agent) ? agent.ParentAgentId : null;

    /// <summary>Gets a roster entry by agent id.</summary>
    public bool TryGet(string agentId, out AgentInfo agent)
        => _agents.TryGetValue(agentId, out agent!);

    /// <summary>
    /// The sender's coordination family: the sender, its parent, its siblings
    /// (other children of the same parent), and its children. When
    /// <paramref name="includeSelf"/> is false the sender itself is excluded
    /// (used for <c>to="all"</c> fan-out).
    /// </summary>
    public IReadOnlyList<AgentInfo> GetFamily(string agentId, bool includeSelf = true)
    {
        var members = new Dictionary<string, AgentInfo>(StringComparer.Ordinal);
        if (_agents.TryGetValue(agentId, out var self))
        {
            if (includeSelf) members[self.AgentId] = self;

            if (self.ParentAgentId is not null && _agents.TryGetValue(self.ParentAgentId, out var parent))
            {
                members[parent.AgentId] = parent;

                foreach (var sibling in _agents.Values)
                {
                    // The sender itself matches the sibling predicate; drop it when self is excluded.
                    if (sibling.ParentAgentId == self.ParentAgentId && (includeSelf || sibling.AgentId != agentId))
                        members[sibling.AgentId] = sibling;
                }
            }

            foreach (var child in _agents.Values)
            {
                if (child.ParentAgentId == agentId)
                    members[child.AgentId] = child;
            }
        }

        return members.Values
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.AgentId, StringComparer.Ordinal)
            .ToArray();
    }

    private void FireChanged()
    {
        Action<AgentRoster>[] handlers;
        lock (_gate)
        {
            if (_changeHandlers is null || _changeHandlers.Count == 0) return;
            handlers = [.. _changeHandlers];
        }

        var snapshot = new AgentRoster(GetRoster());
        foreach (var handler in handlers)
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // A single observer must not break roster bookkeeping.
            }
        }
    }
}
