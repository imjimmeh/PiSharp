namespace PiSharp.PlanMode;

/// <summary>
/// Session-level plan-mode phase. <see cref="Inactive"/> is the normal state;
/// <see cref="Planning"/> restricts the model to read-only tools and (optionally)
/// a planning model; <see cref="Executing"/> runs with full tools and the approved
/// plan injected; <see cref="Aborted"/> is the terminal state of a session's plan
/// lifecycle after <c>/plan abort</c> (tools/model already restored, aborted plan
/// file path retained for client rendering).
/// </summary>
public enum PlanModePhase
{
    Inactive,
    Planning,
    Executing,
    Aborted
}

/// <summary>
/// Immutable snapshot of the plan-mode machine. <see cref="RestrictedToolNames"/>
/// is the effective active tool set while <see cref="Planning"/> (empty otherwise);
/// <see cref="PlanningModel"/> is the resolved planning model id (null = no switch);
/// <see cref="PlanFile"/> is the current plan file path (deterministic from the
/// session id; null only when the machine has never been entered).
/// </summary>
public sealed record PlanModeState(
    PlanModePhase Phase,
    IReadOnlyList<string> RestrictedToolNames,
    string? PlanningModel,
    string? PlanFile);
