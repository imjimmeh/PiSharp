namespace PiSharp.Runtime.Telemetry;

/// <summary>
/// Canonical telemetry event names emitted by the core instrumentor and reused
/// as the <c>kind</c> values in the local <c>metrics.jsonl</c> event records.
/// </summary>
public static class TelemetryEventNames
{
    public const string SessionStarted      = "session_started";
    public const string SessionEnded        = "session_ended";
    public const string TurnEnded           = "turn_ended";
    public const string ToolFailed          = "tool_failed";
    public const string ToolRetried         = "tool_retried";
    public const string CompactionRan       = "compaction_ran";
    public const string ExtensionLoaded     = "extension_loaded";
    public const string ExtensionLoadFailed = "extension_load_failed";
    public const string DaemonStarted       = "daemon_started";
    public const string DaemonStopped       = "daemon_stopped";
}

/// <summary>
/// OTel <c>Meter("PiSharp")</c> instrument names used by the core instrumentor.
/// </summary>
public static class TelemetryInstrumentNames
{
    public const string TurnDuration        = "pisharp.turn.duration";
    public const string TurnTokens          = "pisharp.turn.tokens";
    public const string ToolDuration        = "pisharp.tool.duration";
    public const string ToolCalls           = "pisharp.tool.calls";
    public const string ToolFailures        = "pisharp.tool.failures";
    public const string ToolRetries         = "pisharp.tool.retries";
    public const string Compactions         = "pisharp.compactions";
    public const string ExtensionLoads      = "pisharp.extension.loads";
    public const string ExtensionLoadDuration = "pisharp.extension.load.duration";
    public const string SessionActive       = "pisharp.session.active";
    public const string TurnActive          = "pisharp.turn.active";
    public const string AttachCount         = "pisharp.attach.count";
}
