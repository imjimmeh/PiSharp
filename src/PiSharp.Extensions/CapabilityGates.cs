namespace PiSharp.Extensions;

/// <summary>
/// A process-spawn request that must pass a capability gate before execution.
/// <see cref="Kind"/> distinguishes the spawn surface ("shell" for extension
/// <c>IExecutionEnv.ExecAsync</c>, "mcp" for stdio MCP server processes);
/// <see cref="SourceId"/> carries the originating extension id (provenance) for
/// extension-contributed servers.
/// </summary>
public sealed record SpawnRequest(
    string Kind,
    string Command,
    IReadOnlyList<string>? Args = null,
    string? SourceId = null,
    string? Name = null);

/// <summary>Returns a denial reason when <paramref name="request"/> must be blocked, or null to allow.</summary>
public delegate string? SpawnApproval(SpawnRequest request);

/// <summary>
/// Static seams the permission extension registers so spawn points — extension shell execution
/// via <c>IExecutionEnv</c> and stdio MCP server processes — can fail closed WITHOUT the
/// consumers depending on the permissions plugin assembly. A null gate means "no policy
/// installed; allow", preserving the historic un-gated default posture.
/// </summary>
public static class CapabilityGates
{
    /// <summary>Gate for extension-initiated shell execution (strict-mode fail-closed + ask/deny).</summary>
    public static SpawnApproval? ShellExec { get; set; }

    /// <summary>Gate for stdio MCP server spawns (strict-mode allow-list check).</summary>
    public static SpawnApproval? McpSpawn { get; set; }

    /// <summary>Evaluates an extension shell spawn against the registered gate (null → allow).</summary>
    public static string? EvaluateShell(SpawnRequest request) => ShellExec?.Invoke(request);

    /// <summary>Evaluates a stdio MCP server spawn against the registered gate (null → allow).</summary>
    public static string? EvaluateMcpSpawn(SpawnRequest request) => McpSpawn?.Invoke(request);
}
