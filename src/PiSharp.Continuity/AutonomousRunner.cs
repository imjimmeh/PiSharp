using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// Orchestrates an autonomous run: delivers the instruction, watches the budget
/// at message/turn boundaries, soft-stops (or hard-aborts per
/// <c>autonomous.overshootPolicy</c>), executes quality gates, and always emits
/// <c>autonomous_ended</c> with the end reason and gate results.
/// </summary>
public sealed class AutonomousRunner
{
    public const string KeepAliveReason = "continuity:autonomous";

    private readonly IContinuityStateStore _store;
    private readonly IContinuityEvents _events;
    private readonly IHarnessGateway _harness;
    private readonly GoalService _goals;
    private readonly ContinuityClock _clock;
    private readonly QualityGateRunner _gates;
    private readonly IKeepAliveRegistry _keepAlive;
    private readonly bool _keepAliveOnAutonomous;

    private readonly string _overshootPolicy;          // "soft" | "hard"
    private readonly int _defaultMaxTurns;
    private readonly long? _defaultMaxTokens;
    private readonly int _defaultTimeoutMinutes;
    private readonly string _continuationNudge;
    private readonly string _gateFailurePolicy;        // "stop" | "continue-with-feedback"
    private readonly int _gateTimeoutSeconds;
    private readonly int _gateRetries;
    private readonly int _gateBackoffSeconds;

    private AutonomousRunState? _run;
    private readonly object _gate = new();

    public AutonomousRunState? Run { get { lock (_gate) return _run; } }

    public AutonomousRunner(
        IContinuityStateStore store,
        IContinuityEvents events,
        IHarnessGateway harness,
        GoalService goals,
        ContinuityClock clock,
        QualityGateRunner gates,
        IKeepAliveRegistry keepAlive,
        bool keepAliveOnAutonomous = true,
        string overshootPolicy = "soft",
        int defaultMaxTurns = 10,
        long? defaultMaxTokens = null,
        int defaultTimeoutMinutes = 30,
        string continuationNudge = "Continue working on the goal. Budget remains.",
        string gateFailurePolicy = "continue-with-feedback",
        int gateTimeoutSeconds = 60,
        int gateRetries = 3,
        int gateBackoffSeconds = 5)
    {
        _store = store;
        _events = events;
        _harness = harness;
        _goals = goals;
        _clock = clock;
        _gates = gates;
        _keepAlive = keepAlive;
        _keepAliveOnAutonomous = keepAliveOnAutonomous;
        _overshootPolicy = overshootPolicy;
        _defaultMaxTurns = defaultMaxTurns;
        _defaultMaxTokens = defaultMaxTokens;
        _defaultTimeoutMinutes = defaultTimeoutMinutes;
        _continuationNudge = continuationNudge;
        _gateFailurePolicy = gateFailurePolicy;
        _gateTimeoutSeconds = gateTimeoutSeconds;
        _gateRetries = gateRetries;
        _gateBackoffSeconds = gateBackoffSeconds;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        AutonomousRunState? run;
        run = await _store.LoadAutonomousAsync(ct);
        lock (_gate) _run = run?.Running == true ? run : null; // a persisted non-running state is terminal
    }

    public async Task<AutonomousStartResult> StartAsync(AutonomousCommand cmd, string? goalObjective, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_run is { Running: true })
                throw new InvalidOperationException("An autonomous run is already in progress.");
        }

        var instruction = string.IsNullOrWhiteSpace(cmd.Message)
            ? goalObjective ?? throw new InvalidOperationException("An autonomous run needs a message or an active goal.")
            : cmd.Message;

        var now = _clock.UtcNow;
        var runId = Guid.NewGuid().ToString();
        var maxTurns = cmd.MaxTurns ?? _defaultMaxTurns;
        if (maxTurns <= 0) maxTurns = _defaultMaxTurns;
        var maxTokens = NormalizeBudget(cmd.MaxTokens ?? _defaultMaxTokens);
        var timeoutMin = cmd.TimeoutMinutes ?? _defaultTimeoutMinutes;
        if (timeoutMin <= 0) timeoutMin = _defaultTimeoutMinutes;
        var gates = cmd.Gates ?? [];

        var run = new AutonomousRunState(
            RunId: runId,
            GoalId: _goals.Goal?.Id,
            Instruction: instruction,
            MaxTurns: maxTurns,
            MaxTokens: maxTokens,
            Deadline: now.AddMinutes(timeoutMin),
            Gates: gates,
            Running: true,
            TurnCount: 0,
            TokensUsed: 0,
            StartedAt: now,
            EndedAt: null,
            EndReason: null,
            GateResults: null);

        lock (_gate) _run = run;
        await PersistAsync(ct);
        if (_keepAliveOnAutonomous) _keepAlive.Add(KeepAliveReason);
        await _harness.SendUserMessageAsync(instruction, triggerTurn: true, ct);
        return new AutonomousStartResult(runId, run);
    }

    /// <summary>
    /// Called on each <c>message_end</c>. Accounts tokens toward the run budget
    /// and, when <c>overshootPolicy = "hard"</c>, aborts a single turn that
    /// crosses the budget mid-run.
    /// </summary>
    public async Task OnAssistantMessageAsync(AssistantMessage message, CancellationToken ct)
    {
        AutonomousRunState? run;
        lock (_gate) run = _run;
        if (run is not { Running: true }) return;
        var usage = message.Usage?.TotalTokens ?? 0;
        if (usage <= 0) return;

        var newTokens = run.TokensUsed + usage;
        var overshot = run.MaxTokens is { } max && newTokens > max;
        run = run with { TokensUsed = newTokens };
        lock (_gate) _run = run;
        await PersistAsync(ct);
        await _events.BudgetUpdatedAsync(run.GoalId, run.RunId, run.TokensUsed, run.MaxTokens ?? 0, run.MaxTurns - run.TurnCount, "autonomous", ct);

        if (overshot && string.Equals(_overshootPolicy, "hard", StringComparison.OrdinalIgnoreCase))
        {
            await EndAsync(AutonomousEndReason.BudgetExhausted, ct);
            await _harness.AbortAsync(ct);
        }
    }

    /// <summary>
    /// Called on each <c>turn_end</c>/<c>settled</c>. If budget remains, we are
    /// under <c>MaxTurns</c>, before the deadline, and the goal is not complete,
    /// queue the next continuation turn. Otherwise finish (soft-stop semantics
    /// prime parity: reaching a limit is not success).
    /// </summary>
    public async Task OnTurnEndAsync(CancellationToken ct)
    {
        AutonomousRunState? run;
        lock (_gate) run = _run;
        if (run is not { Running: true }) return;

        var now = _clock.UtcNow;
        var goalComplete = _goals.Goal?.Status == ContinuityGoalStatus.Complete;
        var budgetRemains = run.MaxTokens is not { } max || run.TokensUsed < max;
        var canContinue = run.TurnCount < run.MaxTurns
            && budgetRemains
            && now < run.Deadline
            && !goalComplete;

        lock (_gate) _run = run with { TurnCount = run.TurnCount + 1 };

        if (canContinue)
        {
            await _harness.SendUserMessageAsync(_continuationNudge, triggerTurn: true, ct);
            return;
        }

        var reason = goalComplete ? AutonomousEndReason.Completed
            : now >= run.Deadline ? AutonomousEndReason.Timeout
            : !budgetRemains ? AutonomousEndReason.BudgetExhausted
            : AutonomousEndReason.Completed;
        await EndAsync(reason, ct);
    }

    /// <summary>User-requested hard abort (<c>/autonomous stop</c>).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        AutonomousRunState? run;
        lock (_gate) run = _run;
        if (run is not { Running: true }) return;
        await EndAsync(AutonomousEndReason.Aborted, ct);
    }

    private async Task EndAsync(AutonomousEndReason reason, CancellationToken ct)
    {
        AutonomousRunState? run;
        lock (_gate) { run = _run; if (run is not { Running: true }) return; }

        var gateResults = await TryRunGateResultsAsync(run, reason, ct);
        var finalReason = reason;
        if (gateResults is { } results && results.Any(r => !r.Passed))
        {
            finalReason = AutonomousEndReason.GateFailed;
        }

        var now = _clock.UtcNow;
        run = run with { Running = false, EndedAt = now, EndReason = finalReason, GateResults = gateResults };
        lock (_gate) _run = run;
        await PersistAsync(ct);
        if (_keepAliveOnAutonomous) _keepAlive.Remove(KeepAliveReason);
        await _events.AutonomousEndedAsync(run, ct);
    }

    private async Task<IReadOnlyList<QualityGateResult>?> TryRunGateResultsAsync(AutonomousRunState run, AutonomousEndReason reason, CancellationToken ct)
    {
        if (run.Gates.Count == 0) return null;
        // Gates run only when the natural finish would be Completed or BudgetExhausted.
        if (reason != AutonomousEndReason.Completed && reason != AutonomousEndReason.BudgetExhausted)
            return null;

        var results = new List<QualityGateResult>();
        foreach (var gate in run.Gates)
        {
            results.Add(await _gates.RunAsync(gate, _gateTimeoutSeconds, _gateRetries, _gateBackoffSeconds, ct));
        }
        return results;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        AutonomousRunState? run;
        lock (_gate) run = _run;
        await _store.SaveAutonomousAsync(run, ct);
    }

    private static long? NormalizeBudget(long? tokens) => tokens is null or <= 0 ? null : tokens;
}
