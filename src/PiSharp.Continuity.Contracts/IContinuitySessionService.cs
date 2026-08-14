namespace PiSharp.Continuity.Contracts;

/// <summary>
/// Facade exercised by the daemon wire handlers (<c>set_goal</c>,
/// <c>get_goal</c>, <c>schedule_job</c>, <c>list_jobs</c>, <c>cancel_job</c>,
/// <c>autonomous</c>, <c>get_continuity_state</c>) and by SDK consumers. The
/// plugin registers its implementation via the per-runtime service registry;
/// handlers resolve it and return <c>continuity_unavailable</c> when absent.
/// </summary>
public interface IContinuitySessionService
{
    Task<ContinuityGoalResult> SetGoalAsync(string objective, long? maxTokens, CancellationToken ct);
    Task<ContinuityGoalResult> GetGoalAsync(CancellationToken ct);
    Task<ContinuityJobResult> ScheduleJobAsync(string name, string cron, string prompt, bool? enabled, CancellationToken ct);
    Task<ContinuityJobListResult> ListJobsAsync(CancellationToken ct);
    Task<ContinuityJobResult> CancelJobAsync(string jobId, CancellationToken ct);
    Task<AutonomousStartResult> StartAutonomousAsync(AutonomousCommand cmd, CancellationToken ct);
    Task<ContinuityStateResult> GetStateAsync(CancellationToken ct);
}
