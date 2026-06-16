using System.Collections.Concurrent;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;

namespace PiSharp.Runtime.Subagents;

public sealed class SubagentSessionService : IAsyncDisposable
{
    private readonly SessionRuntime _runtime;
    private readonly ConcurrentDictionary<string, SubagentSessionHandle> _handles = new();
    private readonly ConcurrentDictionary<string, SessionSubscriberState> _subscribers = new();

    private sealed class SessionSubscriberState
    {
        public List<Func<object, CancellationToken, Task>> Callbacks { get; } = [];
        public IDisposable? HarnessSubscription { get; set; }
    }

    public SubagentSessionService(SessionRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IDisposable Subscribe(string sessionId, Func<object, Task> callback)
        => Subscribe(sessionId, (evt, _) => callback(evt));

    public IDisposable Subscribe(string sessionId, Func<object, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var state = _subscribers.GetOrAdd(sessionId, _ => new SessionSubscriberState());
        lock (state.Callbacks) { state.Callbacks.Add(callback); }
        return new Subscription(() =>
        {
            lock (state.Callbacks) { state.Callbacks.Remove(callback); }
        });
    }

    public async Task<SubagentSessionHandle> CreateAsync(SubagentSessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var createOptions = _runtime.CreateOptions with
        {
            Id = null,
            PersistImmediately = true,
            ParentSessionPath = options.ParentSessionPath ?? _runtime.CreateOptions.ParentSessionPath
        };
        var childSession = await _runtime.SessionRepo.CreateAsync(createOptions, cancellationToken);

        try
        {
            if (options.SessionName is not null)
                await childSession.AppendSessionNameAsync(options.SessionName, cancellationToken);

            var model = options.Model ?? _runtime.Harness.Model;
            var thinkingLevel = options.ThinkingLevel ?? _runtime.Harness.ThinkingLevel;
            var harness = _runtime.HarnessFactory(childSession);
            await harness.SetModelAsync(model, "subagent", cancellationToken);
            await harness.SetThinkingLevelAsync(thinkingLevel, cancellationToken);

            var handle = new SubagentSessionHandle
            {
                SessionId = childSession.Metadata.Id,
                Session = childSession,
                Harness = harness,
            };

            _handles[childSession.Metadata.Id] = handle;

            var subscriberState = _subscribers.GetOrAdd(childSession.Metadata.Id, _ => new SessionSubscriberState());
            subscriberState.HarnessSubscription = harness.Subscribe(async (harnessEvent, ct) =>
            {
                if (!_subscribers.TryGetValue(handle.SessionId, out var state))
                    return;

                if (harnessEvent is not AgentHarnessEvent.Core core)
                    return;

                var jsEvents = JsPiSubagentEventTranslator.Translate(core.Event);

                List<Func<object, CancellationToken, Task>> callbacks;
                lock (state.Callbacks) { callbacks = state.Callbacks.ToList(); }

                foreach (var evt in jsEvents)
                {
                    foreach (var cb in callbacks)
                    {
                        try { await cb(evt, ct); }
                        catch { }
                    }
                }
            });

            return handle;
        }
        catch
        {
            _subscribers.TryRemove(childSession.Metadata.Id, out _);
            _handles.TryRemove(childSession.Metadata.Id, out _);
            try { await _runtime.SessionRepo.DeleteAsync(childSession.Metadata, CancellationToken.None); }
            catch { }
            throw;
        }
    }

    public async Task DisposeAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_subscribers.TryRemove(sessionId, out var state))
        {
            state.HarnessSubscription?.Dispose();
            lock (state.Callbacks) { state.Callbacks.Clear(); }
        }
        if (_handles.TryRemove(sessionId, out var handle))
            await handle.DisposeAsync();
    }

    public async Task DisposeAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var subscriberStates = _subscribers.ToArray();
        _subscribers.Clear();
        foreach (var item in subscriberStates)
        {
            item.Value.HarnessSubscription?.Dispose();
            lock (item.Value.Callbacks) { item.Value.Callbacks.Clear(); }
        }

        var handles = _handles.ToArray();
        _handles.Clear();
        foreach (var item in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await item.Value.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
        => await DisposeAllAsync(CancellationToken.None);

    public async Task<SubagentPromptResult> PromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var handle = GetRequiredHandle(sessionId);

        var assistant = await handle.Harness.PromptAsync(prompt, cancellationToken);
        await handle.Harness.WaitForIdleAsync();
        var context = await handle.Session.BuildContextAsync(cancellationToken);

        return new SubagentPromptResult(sessionId, assistant, context.Messages);
    }

    public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetRequiredHandle(sessionId).Harness.Steer(AgentMessages.User(text));
        return Task.CompletedTask;
    }

    public Task FollowUpAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetRequiredHandle(sessionId).Harness.FollowUp(AgentMessages.User(text));
        return Task.CompletedTask;
    }

    public Task AbortAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetRequiredHandle(sessionId).Harness.Abort();
        return Task.CompletedTask;
    }

    public Task CompactAsync(string sessionId, string? instructions, CancellationToken cancellationToken)
        => GetRequiredHandle(sessionId).Harness.CompactAsync(instructions, cancellationToken);

    public Task SetModelAsync(string sessionId, ModelDescriptor model, CancellationToken cancellationToken)
        => GetRequiredHandle(sessionId).Harness.SetModelAsync(model, "subagent", cancellationToken);

    public Task SetThinkingLevelAsync(string sessionId, ThinkingLevel level, CancellationToken cancellationToken)
        => GetRequiredHandle(sessionId).Harness.SetThinkingLevelAsync(level, cancellationToken);

    public SubagentSessionHandle? GetHandle(string sessionId)
    {
        _handles.TryGetValue(sessionId, out var handle);
        return handle;
    }

    private SubagentSessionHandle GetRequiredHandle(string sessionId)
        => _handles.TryGetValue(sessionId, out var handle)
            ? handle
            : throw new InvalidOperationException($"Unknown subagent session: {sessionId}");

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private int _disposed;

        public Subscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _unsubscribe();
        }
    }
}
