using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;

namespace PiSharp.Agent.Harness.LoopEvents;

internal enum HarnessEventKind
{
    CoreLoop,
    Own,
    BeforeAgentStart,
    BeforeToolMiddleware,
    AfterToolMiddleware
}

internal sealed class HarnessEventContext
{
    public HarnessEventContext(
        AgentHarnessEvent @event,
        HarnessEventKind kind,
        Func<AgentMessage, Task> queueWriteOrAppendAsync,
        Func<CancellationToken, Task> flushWritesAsync,
        Action<AgentHarnessPhase> setPhase,
        Func<ExtensionEvent, CancellationToken, Task>? dispatchExtensionEventAsync,
        IReadOnlyList<OwnedExtensionRegistration<ExtensionMiddleware>> middleware,
        IReadOnlyList<Func<AgentHarnessEvent, CancellationToken, Task>> listeners,
        ILogger logger,
        int harnessId,
        int extensionHandlerCount,
        ExtensionEvent? extensionEvent = null,
        BeforeToolCallContext? beforeToolCall = null,
        AfterToolCallContext? afterToolCall = null)
    {
        Event = @event;
        Kind = kind;
        CoreEvent = (@event as AgentHarnessEvent.Core)?.Event;
        OwnEvent = (@event as AgentHarnessEvent.Own)?.Event;
        ExtensionEvent = extensionEvent ?? ExtensionEventMapper.Map(@event);
        QueueWriteOrAppendAsync = queueWriteOrAppendAsync;
        FlushWritesAsync = flushWritesAsync;
        SetPhase = setPhase;
        DispatchExtensionEventAsync = dispatchExtensionEventAsync;
        Middleware = middleware;
        Listeners = listeners;
        Logger = logger;
        HarnessId = harnessId;
        ExtensionHandlerCount = extensionHandlerCount;
        EventName = OwnEvent switch
        {
            AgentHarnessOwnEvent.ThinkingLevelSelect => nameof(AgentHarnessOwnEvent.ThinkingLevelSelect),
            AgentHarnessOwnEvent.ThinkingLevelChanged => nameof(AgentHarnessOwnEvent.ThinkingLevelChanged),
            _ => OwnEvent?.GetType().Name ?? CoreEvent?.GetType().Name ?? "Unknown"
        };
        BeforeToolCall = beforeToolCall;
        AfterToolCall = afterToolCall;
    }

    public AgentHarnessEvent Event { get; }
    public HarnessEventKind Kind { get; }
    public AgentEvent? CoreEvent { get; }
    public AgentHarnessOwnEvent? OwnEvent { get; }
    public ExtensionEvent ExtensionEvent { get; }
    public Func<AgentMessage, Task> QueueWriteOrAppendAsync { get; }
    public Func<CancellationToken, Task> FlushWritesAsync { get; }
    public Action<AgentHarnessPhase> SetPhase { get; }
    public Func<ExtensionEvent, CancellationToken, Task>? DispatchExtensionEventAsync { get; }
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionMiddleware>> Middleware { get; }
    public IReadOnlyList<Func<AgentHarnessEvent, CancellationToken, Task>> Listeners { get; }
    public ILogger Logger { get; }
    public int HarnessId { get; }
    public int ExtensionHandlerCount { get; }
    public int ListenerCount => Listeners.Count;
    public string EventName { get; }
    public bool IsThinkingLevelOwnEvent => OwnEvent is AgentHarnessOwnEvent.ThinkingLevelSelect or AgentHarnessOwnEvent.ThinkingLevelChanged;
    public BeforeToolCallContext? BeforeToolCall { get; }
    public AfterToolCallContext? AfterToolCall { get; }
    public BeforeToolCallResult? BeforeToolCallResult { get; set; }
    public AfterToolCallResult? AfterToolCallResult { get; set; }
}
