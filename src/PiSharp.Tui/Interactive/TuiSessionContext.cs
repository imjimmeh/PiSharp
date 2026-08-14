namespace PiSharp.Tui.Interactive;

/// <summary>
/// Mutable state shared across the TUI host collaborators. Passed by reference so every
/// collaborator observes the same live runtime, abort flag and header-expanded flag.
/// </summary>
internal sealed class TuiSessionContext
{
    internal ITuiRuntimeFacade CurrentRuntime { get; set; } = null!;
    internal bool AbortPending { get; set; }
    internal bool HeaderExpanded { get; set; }
}
