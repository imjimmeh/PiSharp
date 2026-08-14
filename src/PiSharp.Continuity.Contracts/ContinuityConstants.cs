namespace PiSharp.Continuity.Contracts;

/// <summary>
/// Client-visible custom-event names emitted by the continuity plugin over the
/// custom-event lane (<c>IExtensionApi.EmitClientEventAsync</c>).
/// </summary>
public static class ContinuityEventNames
{
    public const string GoalUpdated = "goal_updated";
    public const string BudgetUpdated = "budget_updated";
    public const string AutonomousEnded = "autonomous_ended";
    public const string ScheduledPrompt = "scheduled_prompt";
    public const string HeartbeatTick = "heartbeat_tick";
}

/// <summary>
/// Settings keys under the P23 namespace (<c>pisharp-continuity.*</c>),
/// read via the per-extension scoped settings API. Mirrors the continuity
/// plan §7 schema.
/// </summary>
public static class ContinuitySettingsKeys
{
    public const string GoalMaxTokensPerGoal = "goal.maxTokensPerGoal";
    public const string GoalRequireExplicitStart = "goal.requireExplicitStart";
    public const string HeartbeatEnabled = "heartbeat.enabled";
    public const string HeartbeatIntervalMinutes = "heartbeat.intervalMinutes";
    public const string HeartbeatPromptTemplate = "heartbeat.promptTemplate";
    public const string HeartbeatRequireGoal = "heartbeat.requireGoal";
    public const string CronEnabled = "cron.enabled";
    public const string CronSweepResolutionSeconds = "cron.sweepResolutionSeconds";
    public const string CronTimezone = "cron.timezone";
    public const string CronCatchUpOnResume = "cron.catchUpOnResume";
    public const string CronCatchUpMaxMissed = "cron.catchUpMaxMissed";
    public const string AutonomousEnabled = "autonomous.enabled";
    public const string AutonomousDefaultMaxTurns = "autonomous.defaultMaxTurns";
    public const string AutonomousDefaultMaxTokens = "autonomous.defaultMaxTokens";
    public const string AutonomousDefaultTimeoutMinutes = "autonomous.defaultTimeoutMinutes";
    public const string AutonomousGateTimeoutSeconds = "autonomous.gateTimeoutSeconds";
    public const string AutonomousGateRetries = "autonomous.gateRetries";
    public const string AutonomousGateRetryBackoffSeconds = "autonomous.gateRetryBackoffSeconds";
    public const string AutonomousGateFailurePolicy = "autonomous.gateFailurePolicy";
    public const string AutonomousOvershootPolicy = "autonomous.overshootPolicy";
    public const string AutonomousContinuationNudgeTemplate = "autonomous.continuationNudgeTemplate";
}
