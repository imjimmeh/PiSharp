using PiSharp.Agent.Core.Events;
using PiSharp.Continuity.Contracts;
using PiSharp.Extensions;
using PiSharp.Continuity;

[assembly: ExtensionMetadata(
    "pisharp-continuity",
    Name = "PiSharp Continuity Suite",
    Version = "0.1.0",
    Description = "Goals, heartbeats, cron, and bounded autonomous continuation with crash-safe ticks, persisted daemon-resident via P02 state.",
    SourceId = "pi:extension:continuity")]

namespace PiSharp.Continuity;

/// <summary>
/// <c>pisharp-continuity</c> extension entry. Wires the continuity engine
/// (goal/budget/heartbeat/cron/autonomous) to <c>IExtensionApi</c> for the
/// current session, registers the slash commands, the <c>update_goal</c> tool,
/// the goal prompt contributor, and the <c>message_end</c>/<c>turn_end</c>
/// subscriptions, and exposes <see cref="IContinuitySessionService"/> via
/// <see cref="Service"/> for daemon wire handlers.
/// </summary>
public sealed class ContinuityExtension : IExtension, IContinuitySessionService, IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private IExtensionApi? _api;
    private ContinuitySessionService? _service;
    private ContinuityScheduler? _scheduler;
    private bool _disposed;

    /// <summary>The per-session continuity service facade (daemon wire-handler entry).</summary>
    public ContinuitySessionService? Service => _service;

    // --- IContinuitySessionService delegation (the extension IS the daemon surface, mirroring
    // the plan-mode IPlanModeDaemonSurface pattern; the handler resolves this via ExtensionManager) ---

    public Task<ContinuityGoalResult> SetGoalAsync(string objective, long? maxTokens, CancellationToken ct)
        => _service?.SetGoalAsync(objective, maxTokens, ct) ?? throw new InvalidOperationException("Continuity extension is not initialized.");

    public Task<ContinuityGoalResult> GetGoalAsync(CancellationToken ct)
        => _service?.GetGoalAsync(ct) ?? throw new InvalidOperationException("Continuity extension is not initialized.");

    public Task<ContinuityJobResult> ScheduleJobAsync(string name, string cron, string prompt, bool? enabled, CancellationToken ct)
        => _service?.ScheduleJobAsync(name, cron, prompt, enabled, ct) ?? throw new InvalidOperationException("Continuity extension is not initialized.");

    public Task<ContinuityJobListResult> ListJobsAsync(CancellationToken ct)
        => _service?.ListJobsAsync(ct) ?? throw new InvalidOperationException("Continuity extension is not initialized.");

    public Task<ContinuityJobResult> CancelJobAsync(string jobId, CancellationToken ct)
        => _service?.CancelJobAsync(jobId, ct) ?? throw new InvalidOperationException("Continuity extension is not initialized.");

    public Task<AutonomousStartResult> StartAutonomousAsync(AutonomousCommand cmd, CancellationToken ct)
        => _service?.StartAutonomousAsync(cmd, ct) ?? throw new InvalidOperationException("Continuity extension is not initialized.");

    public Task<ContinuityStateResult> GetStateAsync(CancellationToken ct)
        => _service?.GetStateAsync(ct) ?? throw new InvalidOperationException("Continuity extension is not initialized.");

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        var clock = new ContinuityClock();
        var keepAlive = new InMemoryKeepAliveRegistry();

        var sessionKey = await ResolveSessionKeyAsync(api, cancellationToken);
        var store = new ExtensionStateStoreAdapter(api, sessionKey, clock);
        var events = new ExtensionEventsAdapter(api);
        var harness = new ExtensionHarnessGateway(api);

        var goalService = new GoalService(store, events, keepAlive, clock,
            requireExplicitStart: ReadBool(api, ContinuitySettingsKeys.GoalRequireExplicitStart, false),
            keepAliveOnActiveGoal: true);
        var budgetAccountant = new BudgetAccountant(goalService, events, clock);
        var heartbeat = new HeartbeatService(store, events, harness, goalService, clock, keepAlive,
            requireGoal: ReadBool(api, ContinuitySettingsKeys.HeartbeatRequireGoal, true));
        var gateRunner = new QualityGateRunner(api.Cwd);
        var runner = new AutonomousRunner(store, events, harness, goalService, clock, gateRunner, keepAlive,
            overshootPolicy: ReadString(api, ContinuitySettingsKeys.AutonomousOvershootPolicy, "soft"),
            defaultMaxTurns: ReadInt(api, ContinuitySettingsKeys.AutonomousDefaultMaxTurns, 10),
            defaultTimeoutMinutes: ReadInt(api, ContinuitySettingsKeys.AutonomousDefaultTimeoutMinutes, 30),
            continuationNudge: ReadString(api, ContinuitySettingsKeys.AutonomousContinuationNudgeTemplate, "Continue working on the goal. Budget remains."),
            gateFailurePolicy: ReadString(api, ContinuitySettingsKeys.AutonomousGateFailurePolicy, "continue-with-feedback"),
            gateTimeoutSeconds: ReadInt(api, ContinuitySettingsKeys.AutonomousGateTimeoutSeconds, 60),
            gateRetries: ReadInt(api, ContinuitySettingsKeys.AutonomousGateRetries, 3),
            gateBackoffSeconds: ReadInt(api, ContinuitySettingsKeys.AutonomousGateRetryBackoffSeconds, 5));
        var scheduler = new ContinuityScheduler(store, events, harness, heartbeat, runner, clock, keepAlive,
            sweepResolutionSeconds: ReadInt(api, ContinuitySettingsKeys.CronSweepResolutionSeconds, 15),
            timezoneUtc: string.Equals(ReadString(api, ContinuitySettingsKeys.CronTimezone, "local"), "utc", StringComparison.OrdinalIgnoreCase),
            catchUpOnResume: ReadBool(api, ContinuitySettingsKeys.CronCatchUpOnResume, true),
            catchUpMaxMissed: ReadInt(api, ContinuitySettingsKeys.CronCatchUpMaxMissed, 1));

        _service = new ContinuitySessionService(goalService, scheduler, runner, clock);
        _scheduler = scheduler;

        // Load persisted state and re-arm (create_session re-arm path).
        await goalService.LoadAsync(cancellationToken);
        await heartbeat.LoadAsync(cancellationToken);
        await scheduler.LoadAsync(cancellationToken);
        scheduler.Start();

        var commands = new ContinuitySlashCommands(api.Ui, goalService, scheduler, heartbeat, runner);
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration("goal", "Continuity goal: /goal [objective|start|pause|resume|complete|clear|progress <t>|budget <n>]", commands.OnGoalAsync)));
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration("heartbeat", "Continuity heartbeat: /heartbeat [<minutes>|off]", commands.OnHeartbeatAsync)));
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration("cron", "Continuity cron: /cron [list|add <name> \"<cron>\" \"<prompt>\"|remove|enable|disable]", commands.OnCronAsync)));
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration("autonomous", "Continuity autonomous: /autonomous [--max-turns N --max-tokens N --timeout-min M --gate \"cmd\"] [message]", commands.OnAutonomousAsync)));

        var updateGoal = new UpdateGoalTool(goalService);
        _subscriptions.Add(api.RegisterTool(new ExtensionToolRegistration(
            Name: "update_goal",
            Label: "Update goal",
            Description: "Update the current continuity goal's progress or transition its status (active|paused|complete).",
            ParametersSchema: System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"progress":{"type":"string"},"status":{"type":"string","enum":["active","paused","complete"]}}}""").RootElement.Clone(),
            ExecuteAsync: updateGoal.ExecuteAsync)));


        _subscriptions.Add(api.On(ExtensionEventNames.MessageEnd, (evt, ct) =>
        {
            if (evt.Payload is AgentEvent.MessageEnd messageEnd)
                return budgetAccountant.OnMessageEndAsync(messageEnd, ct);
            return Task.CompletedTask;
        }));
        _subscriptions.Add(api.On(ExtensionEventNames.TurnEnd, (evt, ct) => runner.OnTurnEndAsync(ct)));
        _subscriptions.Add(api.On(ExtensionEventNames.Settled, (evt, ct) => runner.OnTurnEndAsync(ct)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        var scheduler = _scheduler;
        if (scheduler is not null) await Task.Run(() => scheduler.Stop());
        _scheduler = null;
        _service = null;
    }

    private static async Task<string> ResolveSessionKeyAsync(IExtensionApi api, CancellationToken ct)
    {
        try
        {
            var name = await api.Session.GetNameAsync(ct);
            if (!string.IsNullOrWhiteSpace(name)) return Sanitize(name);
        }
        catch (Exception)
        {
            // Fall back to a per-instance default.
        }
        return "default";
    }

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "session" : new string(chars);
    }

    private static bool ReadBool(IExtensionApi api, string key, bool fallback)
    {
        var value = api.Settings.Get<bool>(key);
        return value;
    }

    private static int ReadInt(IExtensionApi api, string key, int fallback)
    {
        var value = api.Settings.Get<int>(key);
        return value > 0 ? value : fallback;
    }

    private static string ReadString(IExtensionApi api, string key, string fallback)
        => api.Settings.Get<string>(key) ?? fallback;
}
