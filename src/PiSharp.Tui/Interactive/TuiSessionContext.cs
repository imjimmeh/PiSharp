using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Harness;

namespace PiSharp.Tui.Interactive;

/// <summary>
/// Mutable state shared across the TUI host collaborators. Passed by reference so every
/// collaborator observes the same live harness, abort flag and header-expanded flag.
/// </summary>
internal sealed class TuiSessionContext
{
    internal AgentHarness<JsonlSessionMetadata> CurrentHarness { get; set; } = null!;
    internal bool AbortPending { get; set; }
    internal bool HeaderExpanded { get; set; }
}
