using System.Text.Json;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Harness;

namespace PiSharp.Runtime.Subagents;

public sealed class SubagentSessionHandle : IAsyncDisposable
{
    private int _disposed;

    public required string SessionId { get; init; }
    public required ISession<JsonlSessionMetadata> Session { get; init; }
    public required AgentHarness<JsonlSessionMetadata> Harness { get; init; }

    /// <summary>Schema-validated structured result produced by the child's terminating <c>yield</c> call.</summary>
    public JsonElement? StructuredResult { get; set; }

    /// <summary>Recursion depth of this child (parent at <c>Depth - 1</c>; top-level spawn is depth 1).</summary>
    public int Depth { get; init; }

    /// <summary>Agent-definition name this child was spawned as (null for anonymous programmatic spawns).</summary>
    public string? AgentName { get; init; }

    /// <summary>Agent-definition name of the session that spawned this child (self-recursion guard).</summary>
    public string? ParentAgentName { get; init; }

    /// <summary>Effective output schema for this child's <c>yield</c> tool, when one was supplied.</summary>
    public JsonElement? OutputSchema { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Harness.Abort();
        await Harness.WaitForIdleAsync();
    }
}
