namespace PiSharp.Continuity.Contracts;

/// <summary>Result of setting/getting the current goal.</summary>
public sealed record ContinuityGoalResult(ContinuityGoal? Goal);

/// <summary>Result of scheduling a job.</summary>
public sealed record ContinuityJobResult(ContinuityJob? Job);

/// <summary>Result of listing jobs.</summary>
public sealed record ContinuityJobListResult(IReadOnlyList<ContinuityJob> Jobs);

/// <summary>Result of starting an autonomous run.</summary>
public sealed record AutonomousStartResult(string RunId, AutonomousRunState State);

/// <summary>Full per-session continuity snapshot.</summary>
public sealed record ContinuityStateResult(
    ContinuityGoal? Goal,
    IReadOnlyList<ContinuityJob> Jobs,
    AutonomousRunState? Run);
