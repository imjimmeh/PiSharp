using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Loops;

namespace PiSharp.Agent;

public sealed class Agent
{
    private readonly MutableAgentState _state;
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private readonly List<Func<AgentEvent, CancellationToken, Task>> _listeners = [];
    private readonly List<Task> _pendingNotifications = [];
    private readonly object _notificationGate = new();
    private readonly object _flushGate = new();
    private ActiveRun? _activeRun;
    private readonly ILogger _logger;
    private AgentEvent.MessageUpdate? _bufferedUpdate;
    private Timer? _flushTimer;
    private const int FlushIntervalMs = 30;

    public Agent(AgentOptions options, ILoggerFactory? loggerFactory = null)
    {
        _state = MutableAgentState.Create(options.InitialState);
        _steeringQueue = new PendingMessageQueue(options.SteeringMode);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode);
        Options = options;
        _logger = loggerFactory?.CreateLogger<Agent>() ?? NullLogger<Agent>.Instance;
    }

    public AgentOptions Options { get; set; }
    public IAgentState State => _state;
    public CancellationToken? Signal => _activeRun?.AbortController.Token;

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        lock (_notificationGate) _listeners.Add(listener);
        return new Subscription(() =>
        {
            lock (_notificationGate) _listeners.Remove(listener);
        });
    }

    public void Steer(AgentMessage message) => _steeringQueue.Enqueue(message);
    public void FollowUp(AgentMessage message) => _followUpQueue.Enqueue(message);
    public void Abort() => _activeRun?.AbortController.Cancel();
    public Task WaitForIdleAsync() => _activeRun?.Completion ?? Task.CompletedTask;

    public Task PromptAsync(string text, CancellationToken cancellationToken = default)
        => PromptAsync([AgentMessages.User(text)], cancellationToken);

    public async Task PromptAsync(IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        await RunWithLifecycleAsync(signal => AgentLoop.RunAgentLoopAsync(messages, CreateContextSnapshot(), CreateLoopConfig(), ProcessEventAsync, signal), cancellationToken);
    }

    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        var last = _state.Messages.LastOrDefault();
        if (last is null) throw new InvalidOperationException("No messages to continue from");
        if (last is AssistantMessage)
        {
            var queued = _steeringQueue.Drain();
            if (queued.Count > 0) { await PromptAsync(queued, cancellationToken); return; }
            queued = _followUpQueue.Drain();
            if (queued.Count > 0) { await PromptAsync(queued, cancellationToken); return; }
            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }

        await RunWithLifecycleAsync(signal => AgentLoop.RunAgentLoopContinueAsync(CreateContextSnapshot(), CreateLoopConfig(), ProcessEventAsync, signal), cancellationToken);
    }

    private async Task RunWithLifecycleAsync(Func<CancellationToken, Task<IReadOnlyList<AgentMessage>>> run, CancellationToken cancellationToken)
    {
        var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeRun = new ActiveRun(abort, completion.Task);
        _state.IsStreaming = true;
        _state.StreamingMessage = null;
        _state.ErrorMessage = null;
        try
        {
            await run(abort.Token);
            FlushBufferedMessageUpdate();
            await WaitForPendingNotificationsAsync();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent invocation failed");
            FlushBufferedMessageUpdate();
            completion.TrySetException(exception);
            throw;
        }
        finally
        {
            _state.IsStreaming = false;
            _state.StreamingMessage = null;
            _state.PendingToolCalls = new HashSet<string>();
            _activeRun = null;
            abort.Dispose();

            lock (_flushGate)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                _bufferedUpdate = null;
            }
        }
    }

    private AgentContext CreateContextSnapshot()
        => new(_state.SystemPrompt, _state.Messages.ToArray(), _state.Tools.ToArray());

    private AgentLoopConfig CreateLoopConfig()
        => Options.LoopConfig with
        {
            GetSteeringMessages = _ => Task.FromResult<IReadOnlyList<AgentMessage>>(_steeringQueue.Drain()),
            GetFollowUpMessages = _ => Task.FromResult<IReadOnlyList<AgentMessage>>(_followUpQueue.Drain())
        };

    private void ProcessEventAsync(AgentEvent @event)
    {
        Reduce(@event);

        if (@event is AgentEvent.MessageUpdate update)
        {
            lock (_flushGate)
            {
                _bufferedUpdate = update;
                if (_flushTimer is null)
                {
                    _flushTimer = new Timer(_ => FlushBufferedMessageUpdate(), null, FlushIntervalMs, Timeout.Infinite);
                }
            }
            return;
        }

        lock (_flushGate)
        {
            if (_flushTimer is not null)
            {
                _flushTimer.Dispose();
                _flushTimer = null;
            }

            if (_bufferedUpdate is not null)
            {
                var buffered = _bufferedUpdate;
                _bufferedUpdate = null;
                NotifyEvent(buffered);
            }
        }

        NotifyEvent(@event);
    }

    private void FlushBufferedMessageUpdate()
    {
        AgentEvent.MessageUpdate? buffered;
        lock (_flushGate)
        {
            buffered = _bufferedUpdate;
            _bufferedUpdate = null;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        if (buffered is not null)
        {
            NotifyEvent(buffered);
        }
    }

    private void NotifyEvent(AgentEvent @event)
    {
        var signal = _activeRun?.AbortController.Token ?? CancellationToken.None;
        var task = NotifyListenersAsync(@event, signal);
        lock (_notificationGate) _pendingNotifications.Add(task);
        _ = task.ContinueWith(_ =>
        {
            lock (_notificationGate) _pendingNotifications.Remove(task);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task NotifyListenersAsync(AgentEvent @event, CancellationToken cancellationToken)
    {
        Func<AgentEvent, CancellationToken, Task>[] listeners;
        lock (_notificationGate) listeners = _listeners.ToArray();
        foreach (var listener in listeners)
        {
            await listener(@event, cancellationToken);
        }
    }

    private async Task WaitForPendingNotificationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_notificationGate) pending = _pendingNotifications.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    private void Reduce(AgentEvent @event)
    {
        switch (@event)
        {
            case AgentEvent.MessageStart(var message):
                _state.StreamingMessage = message;
                break;
            case AgentEvent.MessageUpdate(var message, _):
                _state.StreamingMessage = message;
                break;
            case AgentEvent.MessageEnd(var message):
                _state.StreamingMessage = null;
                _state.Messages.Add(message);
                break;
            case AgentEvent.ToolExecutionStart(var id, _, _):
                _state.PendingToolCalls = _state.PendingToolCalls.Append(id).ToHashSet();
                break;
            case AgentEvent.ToolExecutionEnd(var id, _, _, _):
                _state.PendingToolCalls = _state.PendingToolCalls.Where(existing => existing != id).ToHashSet();
                break;
            case AgentEvent.TurnEnd(var message, _) when message is AssistantMessage { ErrorMessage: not null } assistant:
                _state.ErrorMessage = assistant.ErrorMessage;
                break;
            case AgentEvent.AgentEnd:
                _state.StreamingMessage = null;
                break;
        }
    }

    private void EnsureIdle()
    {
        if (_activeRun is not null) throw new InvalidOperationException("Agent is already processing.");
    }
}
