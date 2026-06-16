using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Harness;

namespace PiSharp.Runtime.Subagents;

public sealed class SubagentSessionHandle : IAsyncDisposable
{
    private int _disposed;

    public required string SessionId { get; init; }
    public required ISession<JsonlSessionMetadata> Session { get; init; }
    public required AgentHarness<JsonlSessionMetadata> Harness { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Harness.Abort();
        await Harness.WaitForIdleAsync();
    }
}
