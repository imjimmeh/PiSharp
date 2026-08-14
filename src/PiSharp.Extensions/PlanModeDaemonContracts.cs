namespace PiSharp.Extensions;

/// <summary>
/// Wire-safe snapshot of the plan-mode state machine, shared across the extension ALC boundary so
/// the daemon (PiSharp.Server) can surface plan mode to clients without referencing the plan-mode
/// plugin assembly. Phase is the snake_case form of <c>PiSharp.PlanMode.PlanModePhase</c>
/// (<c>inactive</c>|<c>planning</c>|<c>executing</c>|<c>aborted</c>).
/// </summary>
public sealed record ExtensionPlanModeState(
    string Phase,
    IReadOnlyList<string> RestrictedToolNames,
    string? PlanningModel,
    string? PlanFile);

/// <summary>
/// App-base bridge implemented by the <c>plan-mode</c> extension. Lets the daemon drive the
/// plugin-owned state machine — the plugin remains authoritative (tool restriction, model switch,
/// and plan-file writes are applied by the machine itself, and every transition emits
/// <c>plan_mode_changed</c> through the C3 client-event lane) — and read attach-time snapshots.
/// </summary>
public interface IPlanModeDaemonSurface
{
    /// <summary>
    /// Applies a phase transition. <paramref name="phase"/> is one of <c>planning</c> (enter),
    /// <c>executing</c> (approve), <c>aborted</c> (abort), or <c>inactive</c> (end). Illegal
    /// transitions (e.g. approve with no captured plan body) and unknown phase names throw
    /// <see cref="InvalidOperationException"/>/<see cref="ArgumentException"/>; entering a phase
    /// the machine is already in is a no-op returning the current state.
    /// </summary>
    Task<ExtensionPlanModeState> ApplyPhaseAsync(string phase, CancellationToken cancellationToken = default);

    /// <summary>Current machine snapshot (never throws).</summary>
    ExtensionPlanModeState Current { get; }
}
