using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;

namespace PiSharp.Runtime.Telemetry;

/// <summary>
/// Maps the <c>AgentHarness</c> event stream onto the canonical telemetry
/// instruments and event records (§4.3). Feeds any <see cref="ITelemetrySink"/>s
/// registered on the backing <see cref="IExtensionTelemetryApi"/>
/// (e.g. the local <c>metrics.jsonl</c> file) and the OTel-native
/// <c>Meter("PiSharp")</c>/<c>ActivitySource</c> surface.
/// </summary>
public sealed class HarnessTelemetryInstrumentor
{
    private readonly IExtensionTelemetryApi _telemetry;
    private readonly TimeProvider _time;
    private readonly string? _sessionId;
    private readonly string? _model;

    private DateTimeOffset? _turnStart;
    private readonly Dictionary<string, DateTimeOffset> _toolStarts = new(StringComparer.Ordinal);

    public HarnessTelemetryInstrumentor(
        IExtensionTelemetryApi telemetry,
        string? sessionId = null,
        string? model = null,
        TimeProvider? time = null)
    {
        _telemetry = telemetry;
        _sessionId = sessionId;
        _model = model;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Async subscription callback compatible with
    /// <c>AgentHarness.Subscribe(Func&lt;AgentHarnessEvent, CancellationToken, Task&gt;)</c>.
    /// </summary>
    public Task OnEventAsync(AgentHarnessEvent evt, CancellationToken cancellationToken = default)
    {
        Handle(evt);
        return Task.CompletedTask;
    }

    /// <summary>Synchronously processes a single harness event into telemetry records.</summary>
    public void Handle(AgentHarnessEvent evt)
    {
        switch (evt)
        {
            case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionStart sessionStart }:
                HandleSessionStart(sessionStart);
                break;
            case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionShutdown }:
                HandleSessionEnd();
                break;
            case AgentHarnessEvent.Core { Event: AgentEvent.TurnStart }:
                HandleTurnStart();
                break;
            case AgentHarnessEvent.Core { Event: AgentEvent.TurnEnd turnEnd }:
                HandleTurnEnd(turnEnd);
                break;
            case AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionStart toolStart }:
                HandleToolStart(toolStart);
                break;
            case AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionEnd toolEnd }:
                HandleToolEnd(toolEnd);
                break;
            case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.AutoRetryStart autoRetry }:
                HandleAutoRetry(autoRetry);
                break;
            case AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionEnd compaction }:
                HandleCompaction(compaction);
                break;
        }
    }

    private void HandleSessionStart(AgentHarnessOwnEvent.SessionStart sessionStart)
    {
        _telemetry.EmitEvent(TelemetryEventNames.SessionStarted, new Dictionary<string, object?>
        {
            ["reason"] = sessionStart.Reason,
            ["sessionId"] = _sessionId,
        });
        _telemetry.IncrementCounter(TelemetryInstrumentNames.SessionActive, 1);
    }

    private void HandleSessionEnd()
    {
        _telemetry.EmitEvent(TelemetryEventNames.SessionEnded, Attr("sessionId", _sessionId));
        _telemetry.IncrementCounter(TelemetryInstrumentNames.SessionActive, -1);
    }

    private void HandleTurnStart()
    {
        _turnStart = _time.GetUtcNow();
        _telemetry.IncrementCounter(TelemetryInstrumentNames.TurnActive, 1);
    }

    private void HandleTurnEnd(AgentEvent.TurnEnd turnEnd)
    {
        var now = _time.GetUtcNow();
        var latencyMs = _turnStart is null ? 0 : Math.Max(0, (now - _turnStart.Value).TotalMilliseconds);
        _turnStart = null;

        var (tokensIn, tokensOut, tokensCache) = ExtractTokens(turnEnd.Message);
        var model = (turnEnd.Message as AssistantMessage)?.Model ?? _model;

        _telemetry.EmitEvent(TelemetryEventNames.TurnEnded, new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["model"] = model,
            ["latencyMs"] = latencyMs,
            ["tokensIn"] = tokensIn,
            ["tokensOut"] = tokensOut,
            ["tokensCache"] = tokensCache,
        });

        _telemetry.RecordMetric(TelemetryInstrumentNames.TurnDuration, latencyMs / 1000.0,
            new Dictionary<string, object?> { ["model"] = model, ["sessionId"] = _sessionId });
        _telemetry.RecordMetric(TelemetryInstrumentNames.TurnTokens, tokensIn + tokensOut + tokensCache,
            new Dictionary<string, object?> { ["model"] = model, ["direction"] = "total" });
        _telemetry.IncrementCounter(TelemetryInstrumentNames.TurnActive, -1);
    }

    private void HandleToolStart(AgentEvent.ToolExecutionStart toolStart)
    {
        _toolStarts[toolStart.ToolCallId] = _time.GetUtcNow();
        _telemetry.IncrementCounter(TelemetryInstrumentNames.ToolCalls, 1, Attr("tool", toolStart.ToolName));
    }

    private void HandleToolEnd(AgentEvent.ToolExecutionEnd toolEnd)
    {
        var now = _time.GetUtcNow();
        var started = _toolStarts.TryGetValue(toolEnd.ToolCallId, out var start) ? start : (DateTimeOffset?)null;
        _toolStarts.Remove(toolEnd.ToolCallId);

        if (started is { } startTime)
        {
            var durationMs = Math.Max(0, (now - startTime).TotalMilliseconds);
            _telemetry.RecordMetric(TelemetryInstrumentNames.ToolDuration, durationMs / 1000.0,
                new Dictionary<string, object?> { ["tool"] = toolEnd.ToolName, ["result"] = toolEnd.IsError ? "error" : "ok" });
        }

        if (toolEnd.IsError)
        {
            _telemetry.IncrementCounter(TelemetryInstrumentNames.ToolFailures, 1,
                new Dictionary<string, object?> { ["tool"] = toolEnd.ToolName, ["error"] = DescribeResult(toolEnd.Result) });
            _telemetry.EmitEvent(TelemetryEventNames.ToolFailed, new Dictionary<string, object?>
            {
                ["tool"] = toolEnd.ToolName,
                ["error"] = DescribeResult(toolEnd.Result),
            });
        }
    }

    private void HandleAutoRetry(AgentHarnessOwnEvent.AutoRetryStart autoRetry)
    {
        _telemetry.IncrementCounter(TelemetryInstrumentNames.ToolRetries, 1);
        _telemetry.EmitEvent(TelemetryEventNames.ToolRetried, new Dictionary<string, object?>
        {
            ["attempt"] = autoRetry.Attempt,
            ["maxAttempts"] = autoRetry.MaxAttempts,
            ["error"] = autoRetry.ErrorMessage,
        });
    }

    private void HandleCompaction(AgentHarnessOwnEvent.CompactionEnd compaction)
    {
        if (compaction.Aborted) return;
        _telemetry.IncrementCounter(TelemetryInstrumentNames.Compactions, 1);
        _telemetry.EmitEvent(TelemetryEventNames.CompactionRan, Attr("reason", compaction.Reason));
    }

    private static (int Input, int Output, int Cache) ExtractTokens(AgentMessage message)
    {
        if (message is AssistantMessage { Usage: { } usage })
        {
            return (usage.Input, usage.Output, usage.CacheRead);
        }

        return (0, 0, 0);
    }

    private static string DescribeResult(object result) => result switch
    {
        string text => text,
        Exception ex => ex.Message,
        _ => result.ToString() ?? string.Empty
    };

    private static IReadOnlyDictionary<string, object?> Attr(string key, object? value)
        => new Dictionary<string, object?> { [key] = value };
}
