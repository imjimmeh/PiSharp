using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tui.Interactive;
using System.Runtime.CompilerServices;

namespace PiSharp.Tui.Interactive.Harness;

internal sealed class TuiHarnessSubscription(
    Func<ITuiRuntimeFacade> getCurrentRuntime,
    RenderStateStore store,
    Action<CancellationToken> scheduleRender,
    Action<Action> dispatch,
    Func<string, IAgentTool?>? resolveTool,
    Func<CancellationToken, Task<TuiSessionSnapshot?>> loadSessionSnapshot,
    Action<TuiSessionSnapshot, bool> applySessionSnapshot,
    TimeSpan? eventBatchInterval = null,
    ILoggerFactory? loggerFactory = null,
    Func<bool>? isAbortPending = null) : IDisposable
{
    private const int EventBatchCapacity = 4096;
    private const int EventBatchSize = 128;
    private readonly TimeSpan _eventBatchInterval = eventBatchInterval ?? TuiTimingOptions.Default.HarnessEventBatchInterval;

    private readonly ILogger<TuiHarnessSubscription> _logger = loggerFactory?.CreateLogger<TuiHarnessSubscription>() ?? NullLogger<TuiHarnessSubscription>.Instance;
    private IDisposable? _subscription;
    private TuiHarnessEventPump? _eventPump;

    public void Bind()
    {
        _subscription?.Dispose();
        StopEventBatching();
        StartEventBatching();
        var runtime = getCurrentRuntime();
        _logger.LogDebug("TUI harness subscription binding harnessId={HarnessId}", RuntimeHelpers.GetHashCode(runtime));
        _subscription = runtime.Subscribe(HandleHarnessEvent);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        StopEventBatching();
    }

    private Task HandleHarnessEvent(AgentHarnessEvent evt, CancellationToken token)
    {
        var enqueueResult = QueueHarnessEvent(evt, token);
        if (DescribeThinkingEvent(evt) is { } thinkingEvent)
        {
            _logger.LogDebug(
                "TUI harness event received harnessId={HarnessId} event={EventName} enqueueResult={EnqueueResult} cancellationRequested={CancellationRequested}",
                RuntimeHelpers.GetHashCode(getCurrentRuntime()),
                thinkingEvent,
                enqueueResult,
                token.IsCancellationRequested);
        }
        return Task.CompletedTask;
    }

    private void StartEventBatching()
    {
        _eventPump = new TuiHarnessEventPump(
            ProcessHarnessEventBatch,
            dispatch,
            _eventBatchInterval,
            EventBatchCapacity,
            EventBatchSize);
    }

    private TuiHarnessEventEnqueueResult QueueHarnessEvent(AgentHarnessEvent evt, CancellationToken token)
        => _eventPump?.Enqueue(evt, token) ?? TuiHarnessEventEnqueueResult.NotQueued;

    private void ProcessHarnessEventBatch(IReadOnlyList<QueuedHarnessEvent> batch)
    {
        var events = new AgentHarnessEvent[batch.Count];
        for (var index = 0; index < batch.Count; index++) events[index] = batch[index].Event;

        var previousState = store.Snapshot();
        var state = previousState.ReduceBatch(events);
        var thinkingEvents = events.Select(DescribeThinkingEvent).Where(description => description is not null).Cast<string>().ToArray();
        if (thinkingEvents.Length > 0)
        {
            _logger.LogDebug(
                "TUI harness batch reducing thinking events harnessId={HarnessId} batchCount={BatchCount} events={ThinkingEvents} previousThinking={PreviousThinking} nextThinking={NextThinking}",
                RuntimeHelpers.GetHashCode(getCurrentRuntime()),
                batch.Count,
                string.Join(",", thinkingEvents),
                previousState.ThinkingLevel,
                state.ThinkingLevel);
        }
        foreach (var queued in batch)
        {
            if (isAbortPending?.Invoke() == true && queued.Event is AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd })
            {
                state = state.TriggerSystemMessageEvent("abort", DateTimeOffset.UtcNow);
            }
        }

        store.Replace(state);
        scheduleRender(default);
        foreach (var queued in batch)
        {
            var token = queued.CancellationToken.IsCancellationRequested ? CancellationToken.None : queued.CancellationToken;
            if (ShouldRefreshSessionSnapshot(queued.Event)) _ = RefreshSessionSnapshotAfterEventAsync(token);
            _ = RenderExtensionToolAsync(queued.Event, token).ContinueWith(task =>
            {
                if (task is { IsCompletedSuccessfully: true, Result: true }) scheduleRender(default);
            }, CancellationToken.None);
        }
    }

    private void StopEventBatching()
    {
        _eventPump?.Dispose();
        _eventPump = null;
    }

    private async Task RefreshSessionSnapshotAfterEventAsync(CancellationToken token)
    {
        try
        {
            var snapshot = await loadSessionSnapshot(token);
            if (snapshot is null) return;
            dispatch(() =>
            {
                applySessionSnapshot(snapshot, true);
                scheduleRender(token);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session snapshot refresh failed");
        }
    }

    private async Task<bool> RenderExtensionToolAsync(AgentHarnessEvent evt, CancellationToken token)
    {
        switch (evt)
        {
            case AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionStart tool }:
                if (resolveTool?.Invoke(tool.ToolName) is not IAgentToolRenderer { HasRenderCall: true } callRenderer) return false;
                var renderedCall = await callRenderer.RenderCallAsync(new ToolRenderRequest(tool.ToolCallId, tool.ToolName, tool.Arguments.Clone(), null, true, false, false, 120), token);
                if (renderedCall is { Lines.Count: > 0 })
                {
                    store.Update(s => s.SetToolRenderedLines(tool.ToolCallId, callLines: renderedCall.Lines));
                    return true;
                }
                return false;
            case AgentHarnessEvent.Core { Event: AgentEvent.ToolExecutionEnd tool }:
                if (resolveTool?.Invoke(tool.ToolName) is not IAgentToolRenderer { HasRenderResult: true } resultRenderer) return false;
                var existing = store.Snapshot().Transcript.LastOrDefault(item => string.Equals(item.ToolCallId, tool.ToolCallId, StringComparison.Ordinal));
                var result = tool.Result as AgentToolResult<object?>;
                var renderedResult = await resultRenderer.RenderResultAsync(new ToolRenderRequest(tool.ToolCallId, tool.ToolName, existing?.ToolArguments?.Clone(), result, false, tool.IsError, existing?.IsExpanded ?? false, 120), token);
                if (renderedResult is { Lines.Count: > 0 })
                {
                    store.Update(s => s.SetToolRenderedLines(tool.ToolCallId, resultLines: renderedResult.Lines));
                    return true;
                }
                return false;
        }

        return false;
    }

    private static bool ShouldRefreshSessionSnapshot(AgentHarnessEvent evt)
        => evt is AgentHarnessEvent.Core { Event: AgentEvent.TurnEnd or AgentEvent.AgentEnd };

    private static string? DescribeThinkingEvent(AgentHarnessEvent evt)
        => evt switch
        {
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelSelect select } => $"{nameof(AgentHarnessOwnEvent.ThinkingLevelSelect)}:{select.Level}",
            AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelChanged changed } => $"{nameof(AgentHarnessOwnEvent.ThinkingLevelChanged)}:{changed.Level}",
            _ => null
        };

}
