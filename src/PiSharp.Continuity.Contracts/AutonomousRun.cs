namespace PiSharp.Continuity.Contracts;

/// <summary>Why an autonomous run ended.</summary>
public enum AutonomousEndReason
{
    Completed,
    BudgetExhausted,
    Timeout,
    GateFailed,
    Aborted,
    Error,
}

/// <summary>
/// A user-defined shell-command quality gate executed between autonomous
/// continuation turns. Runs via <c>System.Diagnostics.Process</c> with a
/// timeout and retries.
/// </summary>
public sealed record QualityGate(
    string Id,
    string Command,            // shell command, e.g. "dotnet build"
    int TimeoutSeconds,        // default continuity.autonomous.gateTimeoutSeconds
    int Retries);              // default continuity.autonomous.gateRetries

/// <summary>Outcome of one quality-gate execution (after its retries).</summary>
public sealed record QualityGateResult(
    string Id,
    bool Passed,
    int Attempts,
    string? OutputTail = null);

/// <summary>
/// The in-memory/persisted state of an autonomous run. Tokens are soft-bounded
/// per turn; the in-flight turn always completes unless
/// <c>overshootPolicy = "hard"</c> forces an abort.
/// </summary>
public sealed record AutonomousRunState(
    string RunId,
    string? GoalId,            // tied goal, if any
    string Instruction,        // continuation instruction delivered as a user message
    int MaxTurns,
    long? MaxTokens,           // null = unlimited
    DateTimeOffset Deadline,   // now + TimeoutMinutes
    IReadOnlyList<QualityGate> Gates,
    bool Running,
    int TurnCount,
    long TokensUsed,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    AutonomousEndReason? EndReason = null,
    IReadOnlyList<QualityGateResult>? GateResults = null);

/// <summary>
/// Facade input to start an autonomous run (daemon <c>autonomous</c> command /
/// <c>/autonomous</c> slash command). A null <see cref="Message"/> means "use
/// the active goal's objective as the instruction".
/// </summary>
public sealed record AutonomousCommand(
    string? Message,              // null → use the active goal's objective
    int? MaxTurns,
    long? MaxTokens,
    int? TimeoutMinutes,
    IReadOnlyList<QualityGate>? Gates); // null → none; empty → none
