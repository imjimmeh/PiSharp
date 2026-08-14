using PiSharp.Continuity.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Continuity;

/// <summary>
/// Wires <see cref="IContinuityStateStore"/> to <c>IExtensionApi.State</c>
/// (P02 user scope). Keys are suffixed with the runtime session id so distinct
/// sessions (each with their own plugin instance) never share keys in the
/// namespace store. schemaVersion = 1.
/// </summary>
public sealed class ExtensionStateStoreAdapter : IContinuityStateStore
{
    public const int SchemaVersion = 1;
    private const string GoalKeyPrefix = "goal.";
    private const string JobsKeyPrefix = "jobs.";
    private const string HeartbeatKeyPrefix = "heartbeat.";
    private const string AutonomousKeyPrefix = "autonomous.";

    private readonly IExtensionStateApi _state;
    private readonly string _sessionKey;
    private readonly ContinuityClock _clock;
    private Task? _flush;

    public ExtensionStateStoreAdapter(IExtensionApi api, string sessionKey, ContinuityClock clock)
    {
        _state = api.State;
        _sessionKey = sessionKey;
        _clock = clock;
    }

    private string G(string prefix) => prefix + _sessionKey;

    public async Task<ContinuityGoal?> LoadGoalAsync(CancellationToken ct)
        => await _state.GetAsync<ContinuityGoal>(G(GoalKeyPrefix), ExtensionStateScope.User, ct);

    public Task SaveGoalAsync(ContinuityGoal? goal, CancellationToken ct)
        => goal is null
            ? _state.RemoveAsync(G(GoalKeyPrefix), ExtensionStateScope.User, ct)
            : _state.SetAsync(G(GoalKeyPrefix), goal, ExtensionStateScope.User, ct);

    public async Task<IReadOnlyList<ContinuityJob>> LoadJobsAsync(CancellationToken ct)
        => await _state.GetAsync<List<ContinuityJob>>(G(JobsKeyPrefix), ExtensionStateScope.User, ct) ?? [];

    public Task SaveJobsAsync(IReadOnlyList<ContinuityJob> jobs, CancellationToken ct)
        => _state.SetAsync(G(JobsKeyPrefix), jobs.ToList(), ExtensionStateScope.User, ct);

    public async Task<HeartbeatState?> LoadHeartbeatAsync(CancellationToken ct)
        => await _state.GetAsync<HeartbeatState>(G(HeartbeatKeyPrefix), ExtensionStateScope.User, ct);

    public Task SaveHeartbeatAsync(HeartbeatState heartbeat, CancellationToken ct)
        => _state.SetAsync(G(HeartbeatKeyPrefix), heartbeat, ExtensionStateScope.User, ct);

    public async Task<AutonomousRunState?> LoadAutonomousAsync(CancellationToken ct)
        => await _state.GetAsync<AutonomousRunState>(G(AutonomousKeyPrefix), ExtensionStateScope.User, ct);

    public Task SaveAutonomousAsync(AutonomousRunState? run, CancellationToken ct)
        => run is null
            ? _state.RemoveAsync(G(AutonomousKeyPrefix), ExtensionStateScope.User, ct)
            : _state.SetAsync(G(AutonomousKeyPrefix), run, ExtensionStateScope.User, ct);

    public Task FlushAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _flush ??= Task.CompletedTask;
        }
        return Task.CompletedTask;
    }
    private readonly object _gate = new();
}

/// <summary>
/// Emits the continuity custom events via <c>IExtensionApi.EmitClientEventAsync</c>
/// (the custom-event lane). The plan's §4.8 events ride this lane to the daemon
/// so attach/replay and per-session envelopes handle them with zero server
/// changes.
/// </summary>
public sealed class ExtensionEventsAdapter : IContinuityEvents
{
    private readonly IExtensionApi _api;

    public ExtensionEventsAdapter(IExtensionApi api) => _api = api;

    public Task GoalUpdatedAsync(ContinuityGoal goal, CancellationToken ct)
        => _api.EmitClientEventAsync(ContinuityEventNames.GoalUpdated, new { goal }, ct);

    public Task BudgetUpdatedAsync(string? goalId, string? runId, long tokensUsed, long budgetTokens, int? remainingTurns, string reason, CancellationToken ct)
        => _api.EmitClientEventAsync(ContinuityEventNames.BudgetUpdated,
            new { goalId, runId, tokensUsed, budgetTokens, remainingTurns, reason }, ct);

    public Task AutonomousEndedAsync(AutonomousRunState run, CancellationToken ct)
        => _api.EmitClientEventAsync(ContinuityEventNames.AutonomousEnded, run, ct);

    public Task ScheduledPromptAsync(string jobId, string name, string tickId, string cron, DateTimeOffset dueAt, DateTimeOffset deliveredAt, string prompt, CancellationToken ct)
        => _api.EmitClientEventAsync(ContinuityEventNames.ScheduledPrompt,
            new { jobId, name, tickId, cron, dueAt, deliveredAt, prompt }, ct);

    public Task HeartbeatTickAsync(string? goalId, DateTimeOffset at, CancellationToken ct)
        => _api.EmitClientEventAsync(ContinuityEventNames.HeartbeatTick, new { goalId, at }, ct);
}

/// <summary>
/// Wires <see cref="IHarnessGateway"/> to the session API surface available on
/// <c>IExtensionApi</c>: <c>Session.SendMessageAsync</c> /
/// <c>SendUserMessageAsync</c>, <c>IsIdleAsync</c>, <c>HasPendingMessagesAsync</c>,
/// <c>AppendEntryAsync</c>, and <c>SessionId</c> via the replacement context
/// (falling back to the session name).
/// </summary>
public sealed class ExtensionHarnessGateway : IHarnessGateway
{
    private readonly IExtensionApi _api;

    public ExtensionHarnessGateway(IExtensionApi api) => _api = api;

    public Task SendUserMessageAsync(string content, bool triggerTurn, CancellationToken ct)
        => _api.Session.SendUserMessageAsync(content,
            triggerTurn ? ExtensionMessageDelivery.NextTurn : ExtensionMessageDelivery.FollowUp, ct);

    public Task<bool> IsIdleAsync(CancellationToken ct) => _api.Session.IsIdleAsync(ct);
    public Task<bool> HasPendingMessagesAsync(CancellationToken ct) => _api.Session.HasPendingMessagesAsync(ct);
    public Task AppendAuditAsync(string customType, object data, CancellationToken ct) => _api.Session.AppendEntryAsync(customType, data, ct);
    public Task AbortAsync(CancellationToken ct) => _api.Session.WaitForIdleAsync(ct);
}
