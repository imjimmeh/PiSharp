namespace PiSharp.AgentMessaging;

/// <summary>
/// Pure target-validation middleware for agent messages: resolves the
/// <c>"all"</c>/<c>"parent"</c> aliases and validates every concrete target
/// against the sender's coordination family before a message is enqueued.
/// Rejections are atomic (all-or-nothing) with the typed
/// <c>agent_message_target_invalid</c> error code.
/// </summary>
public static class MessageTargetValidator
{
    /// <summary>Alias resolving to the sender's parent agent.</summary>
    public const string ParentTarget = "parent";

    /// <summary>Alias resolving to every family member except the sender.</summary>
    public const string AllTarget = "all";

    /// <summary>
    /// Validates <paramref name="rawTargets"/> for <paramref name="senderAgentId"/>.
    /// <paramref name="roleWhitelist"/> restricts which roster roles are
    /// addressable (the <c>agentMessaging.hubRoleWhitelist</c> policy).
    /// </summary>
    public static TargetValidationResult Validate(
        string senderAgentId,
        AgentRosterService roster,
        IReadOnlyList<string> rawTargets,
        IReadOnlyList<string> roleWhitelist)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(rawTargets);

        if (!roster.TryGet(senderAgentId, out _))
            return Invalid("sender is not a roster member");

        if (rawTargets.Count == 0)
            return Invalid("no target specified");

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in rawTargets)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Invalid("empty target specified");

            if (string.Equals(raw, AllTarget, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var member in roster.GetFamily(senderAgentId, includeSelf: false))
                    AddIfAllowed(member, resolved, seen, roleWhitelist, out _);
                continue;
            }

            if (string.Equals(raw, ParentTarget, StringComparison.OrdinalIgnoreCase))
            {
                var parentId = roster.ResolveParent(senderAgentId);
                if (parentId is null)
                    return Invalid($"'{ParentTarget}' resolves to no parent for '{senderAgentId}'");

                if (!roster.TryGet(parentId, out var parent))
                    return Invalid($"parent '{parentId}' is not in the roster");

                AddIfAllowed(parent, resolved, seen, roleWhitelist, out var parentError);
                if (parentError is not null)
                    return Invalid(parentError);
                continue;
            }

            if (!roster.TryGet(raw, out var target))
                return Invalid($"unknown target '{raw}'");

            if (string.Equals(raw, senderAgentId, StringComparison.Ordinal))
                return Invalid($"self-targeting is forbidden ('{raw}')");

            if (!IsInFamily(roster, senderAgentId, raw))
                return Invalid($"target '{raw}' is outside the sender's family");

            AddIfAllowed(target, resolved, seen, roleWhitelist, out var error);
            if (error is not null)
                return Invalid(error);
        }

        if (resolved.Count == 0)
            return Invalid("no reachable targets in the sender's family");

        return new TargetValidationResult(true, resolved);
    }

    private static void AddIfAllowed(
        AgentInfo agent,
        List<string> resolved,
        HashSet<string> seen,
        IReadOnlyList<string> roleWhitelist,
        out string? error)
    {
        error = null;
        if (!seen.Add(agent.AgentId))
            return;

        if (roleWhitelist.Count > 0 && agent.Role is not null && !roleWhitelist.Contains(agent.Role, StringComparer.OrdinalIgnoreCase))
        {
            error = $"target '{agent.AgentId}' has role '{agent.Role}' which is not whitelisted";
            return;
        }

        resolved.Add(agent.AgentId);
    }

    private static bool IsInFamily(AgentRosterService roster, string senderAgentId, string targetId)
        => roster.GetFamily(senderAgentId).Any(member => member.AgentId == targetId);

    private static TargetValidationResult Invalid(string message)
        => new(false, [], AgentMessagingErrorCodes.TargetInvalid, message);
}
