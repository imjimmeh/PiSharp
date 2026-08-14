using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;

namespace PiSharp.Runtime.Subagents;

public sealed class SubagentSessionService : IAsyncDisposable
{
    /// <summary>Tool name the structured-result (<c>yield</c>) contract is keyed on. The plugin's
    /// YieldTool registers under this name and the service captures its terminating result.</summary>
    public const string YieldToolName = "yield";

    /// <summary>Tool name that spawns subagents; stripped from at-cap children so they cannot escalate.</summary>
    public const string SpawnToolName = "task";
    private readonly SessionRuntime _runtime;
    private readonly ConcurrentDictionary<string, SubagentSessionHandle> _handles = new();
    private readonly ConcurrentDictionary<string, SessionSubscriberState> _subscribers = new();
    /// <summary>Sentinel active-tool name used when a depth-cap child must run tool-less: the harness
    /// treats an empty <c>SetActiveTools</c> list as "inherit all", so a set that strips down to
    /// nothing is expressed with an unresolvable name (filtered out at tool resolution).</summary>
    private const string NoToolsSentinel = "__subagents_no_tools__";

    private sealed class SessionSubscriberState
    {
        public List<Func<object, CancellationToken, Task>> Callbacks { get; } = [];
        public IDisposable? HarnessSubscription { get; set; }
    }

    public SubagentSessionService(SessionRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        SubagentRuntimeAccess.Register(this);
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

        // C2 — spawn guardrails are enforced here, at the child-creation boundary, so no caller can
        // bypass the cap by avoiding the coordinator.
        var policy = options.SpawnPolicy ?? SubagentSpawnPolicy.Default;
        var agentName = options.AgentName;
        var parentAgentName = options.ParentAgentName;

        // Rule 1: disabled agent.
        if (agentName is not null && policy.DisabledAgents?.Contains(agentName) == true)
            throw new SubagentSpawnBlockedException(agentName, "disabled");

        // Rule 2: self-recursion — a parent may not spawn an agent with its own name unless it
        // explicitly declares that name in its `spawns` allowlist.
        if (agentName is not null
            && parentAgentName is not null
            && StringComparer.Ordinal.Equals(agentName, parentAgentName)
            && !(policy.ParentSpawns?.Contains(agentName) == true))
            throw new SubagentSpawnBlockedException(agentName, "self-recursion");

        // Rule 3: depth cap.
        var depth = options.Depth;
        if (depth + 1 > policy.MaxRecursionDepth)
            throw new SubagentSpawnBlockedException(agentName ?? "<anonymous>", "max-recursion-depth");

        // Rule 4: spawns allowlist.
        if (agentName is not null && policy.ParentSpawns is not null && !policy.ParentSpawns.Contains(agentName))
            throw new SubagentSpawnBlockedException(agentName, "not-allowed");

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

            // C1 — inject extra tools (e.g. `yield`) before restricting the active set, so a tool
            // registered here is never shadowed by the restriction below.
            if (options.Tools is not null)
            {
                foreach (var tool in options.Tools)
                {
                    if (string.IsNullOrWhiteSpace(tool.Name))
                        throw new ArgumentException("Child tools must declare a name.", nameof(options));
                    harness.RegisterTool("subagent", tool);
                }
            }

            // Skill policy: an explicit selection replaces the parent's inherited set;
            // null inherits the parent's selection (SetSelectedSkills(null) is a no-op).
            if (options.SelectedSkillNames is not null)
                harness.SetSelectedSkills(options.SelectedSkillNames);

            // The child at the depth cap loses the spawn tool so it cannot escalate.
            var atDepthCap = depth + 1 == policy.MaxRecursionDepth;
            IReadOnlyList<string>? activeToolNames = options.ActiveToolNames;
            if (atDepthCap)
            {
                var fullSet = options.ActiveToolNames ?? harness.AllToolNames;
                var stripped = fullSet.Where(name => !StringComparer.Ordinal.Equals(name, SpawnToolName)).ToArray();
                activeToolNames = stripped.Length > 0 ? stripped : [NoToolsSentinel];
            }

            if (activeToolNames is not null)
                harness.SetActiveTools(activeToolNames);
            var handle = new SubagentSessionHandle
            {
                SessionId = childSession.Metadata.Id,
                Session = childSession,
                Harness = harness,
                Depth = depth + 1,
                AgentName = agentName,
                ParentAgentName = parentAgentName,
                OutputSchema = options.OutputSchema,
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
    {
        await DisposeAllAsync(CancellationToken.None);
        SubagentRuntimeAccess.Unregister(this);
    }

    public async Task<SubagentPromptResult> PromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var handle = GetRequiredHandle(sessionId);

        var assistant = await handle.Harness.PromptAsync(prompt, cancellationToken);
        await handle.Harness.WaitForIdleAsync();
        var context = await handle.Session.BuildContextAsync(cancellationToken);

        // C2 — structured-result capture: the terminating `yield` tool result carries the validated
        // JSON in its Details; store it on the handle and surface it on the prompt result.
        var structuredResult = context.Messages
            .OfType<ToolResultMessage>()
            .Where(message => StringComparer.Ordinal.Equals(message.ToolName, YieldToolName) && !message.IsError)
            .LastOrDefault()
            ?.Details switch
        {
            JsonElement element => element,
            JsonDocument document => document.RootElement.Clone(),
            _ => (JsonElement?)null
        };
        handle.StructuredResult = structuredResult;

        return new SubagentPromptResult(sessionId, assistant, context.Messages, structuredResult);
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
