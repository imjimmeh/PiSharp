namespace PiSharp.Runtime.Subagents;

/// <summary>
/// Spawn-policy inputs enforced by <see cref="SubagentSessionService.CreateAsync"/> before a child
/// session is created. The service is the enforcement point so no caller (tool, extension, client)
/// can bypass the cap by not going through the coordinator.
/// </summary>
public sealed record SubagentSpawnPolicy(
    /// <summary>Maximum recursion depth (omp default). A child at depth &gt; this value is blocked.</summary>
    int MaxRecursionDepth = 2,
    /// <summary>Agent names that can never be spawned (settings <c>subagents.disabledAgents</c>).</summary>
    IReadOnlySet<string>? DisabledAgents = null,
    /// <summary>Agent names the requesting parent is allowed to spawn (frontmatter <c>spawns</c>).
    /// Null = unrestricted; empty set = the parent cannot spawn any named agent.</summary>
    IReadOnlySet<string>? ParentSpawns = null)
{
    public static SubagentSpawnPolicy Default { get; } = new();
}

/// <summary>
/// Thrown by <see cref="SubagentSessionService.CreateAsync"/> when a spawn request violates policy:
/// disabled agent, self-recursion, depth cap, or the parent's <c>spawns</c> allowlist.
/// </summary>
public sealed class SubagentSpawnBlockedException : Exception
{
    public SubagentSpawnBlockedException(string agent, string reason)
        : base($"Subagent spawn of '{agent}' was blocked: {reason}.")
    {
        Agent = agent;
        Reason = reason;
    }

    /// <summary>Name of the agent definition whose spawn was rejected.</summary>
    public string Agent { get; }

    /// <summary>Machine-readable policy reason: <c>disabled</c>, <c>self-recursion</c>,
    /// <c>max-recursion-depth</c>, or <c>not-allowed</c>.</summary>
    public string Reason { get; }
}
