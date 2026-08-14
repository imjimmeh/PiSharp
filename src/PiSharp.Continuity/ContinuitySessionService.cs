using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// Facade implementing <see cref="IContinuitySessionService"/> — the surface
/// the daemon wire handlers (<c>set_goal</c>, <c>get_goal</c>, <c>schedule_job</c>,
/// <c>list_jobs</c>, <c>cancel_job</c>, <c>autonomous</c>,
/// <c>get_continuity_state</c>) and SDK consumers call.
/// </summary>
public sealed class ContinuitySessionService : IContinuitySessionService
{
    private readonly GoalService _goals;
    private readonly ContinuityScheduler _scheduler;
    private readonly AutonomousRunner _autonomous;
    private readonly ContinuityClock _clock;

    public ContinuitySessionService(
        GoalService goals,
        ContinuityScheduler scheduler,
        AutonomousRunner autonomous,
        ContinuityClock clock)
    {
        _goals = goals;
        _scheduler = scheduler;
        _autonomous = autonomous;
        _clock = clock;
    }

    public Task<ContinuityGoalResult> SetGoalAsync(string objective, long? maxTokens, CancellationToken ct)
        => _goals.SetGoalAsync(objective, maxTokens, ct);

    public async Task<ContinuityGoalResult> GetGoalAsync(CancellationToken ct)
        => await _goals.GetGoalAsync(ct);

    public async Task<ContinuityJobResult> ScheduleJobAsync(string name, string cron, string prompt, bool? enabled, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var schedule = new CronSchedule(cron);
        var job = new ContinuityJob(
            Id: Guid.NewGuid().ToString(),
            Name: name,
            Cron: cron,
            Prompt: prompt,
            Enabled: enabled ?? true,
            CreatedAt: now,
            NextRunAt: schedule.Next(now));
        await _scheduler.AddJobAsync(job, ct);
        return new ContinuityJobResult(job);
    }

    public async Task<ContinuityJobListResult> ListJobsAsync(CancellationToken ct)
        => new ContinuityJobListResult(_scheduler.Jobs);

    public async Task<ContinuityJobResult> CancelJobAsync(string jobId, CancellationToken ct)
    {
        var removed = await _scheduler.CancelJobAsync(jobId, ct) ? _scheduler.Jobs.FirstOrDefault(j => j.Id == jobId) : null;
        return new ContinuityJobResult(removed);
    }

    public Task<AutonomousStartResult> StartAutonomousAsync(AutonomousCommand cmd, CancellationToken ct)
        => _autonomous.StartAsync(cmd, _goals.Goal?.Objective, ct);

    public async Task<ContinuityStateResult> GetStateAsync(CancellationToken ct)
        => new ContinuityStateResult(await _goals.GetGoalAsync(ct) is { Goal: { } g } ? g : null, _scheduler.Jobs, _autonomous.Run);
}
