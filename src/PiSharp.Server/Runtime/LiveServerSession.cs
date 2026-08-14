using System.Collections.Concurrent;
using System.Threading.Channels;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Harness;
using PiSharp.Extensions;
using PiSharp.Server.Contracts;
namespace PiSharp.Server.Runtime;

public sealed class LiveServerSession : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<ServerEventEnvelope>> _subscribers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _eventCapacity;
    private readonly Func<LiveServerSession, string, string, CancellationToken, Task>? _runtimeSessionChanged;
    private IDisposable? _subscription;
    private IDisposable? _advisorSubscription;
    private CancellationTokenSource _operationAbort = new();
    private string _runtimeSessionId;
    private int _scheduledOperations;
    private int _attachedClients;
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long _sequence;
    private bool _disposed;

    public LiveServerSession(string id, PiSharp.Runtime.SessionRuntime runtime, Func<LiveServerSession, string, string, CancellationToken, Task>? runtimeSessionChanged = null, int eventCapacity = 100_000)
    {
        Id = id;
        Runtime = runtime;
        _runtimeSessionChanged = runtimeSessionChanged;
        _runtimeSessionId = runtime.Session.Metadata.Id;
        _eventCapacity = eventCapacity;
        EventLog = new RetainedEventLog(eventCapacity);
        BindCurrentHarness();
        BindAdvisorEventForwarding();
        Runtime.SetRebindSession(OnRuntimeReboundAsync);
    }

    public string Id { get; }
    public PiSharp.Runtime.SessionRuntime Runtime { get; }
    public string RuntimeSessionId => _runtimeSessionId;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public CancellationToken LifetimeToken => _lifetime.Token;
    public RetainedEventLog EventLog { get; }
    public int AttachedClients => Volatile.Read(ref _attachedClients);
    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
    public bool HasPendingWork => Volatile.Read(ref _scheduledOperations) > 0;

    /// <summary>
    /// Yields retained envelopes with <see cref="ServerEventEnvelope.Sequence"/> at or after
    /// <paramref name="sinceSequence"/> from <see cref="EventLog"/> (skipped entirely when the log
    /// reports a gap), then falls through to the live subscriber tail until cancelled.
    /// </summary>
    public async IAsyncEnumerable<ServerEventEnvelope> ReadEventsAsync(long sinceSequence = 0, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var replay = EventLog.ReplayFrom(sinceSequence);
        if (!replay.Gap)
        {
            foreach (var envelope in replay.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }
        }
        var key = Guid.NewGuid();
        var channel = Channel.CreateBounded<ServerEventEnvelope>(new BoundedChannelOptions(_eventCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        Interlocked.Increment(ref _attachedClients);
        Touch();
        _subscribers[key] = channel;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }
        finally
        {
            if (_subscribers.TryRemove(key, out var removed)) removed.Writer.TryComplete();
            Interlocked.Decrement(ref _attachedClients);
        }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<PiSharp.Runtime.SessionRuntime, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _scheduledOperations);
        Touch();
        var gateTaken = false;
        try
        {
            await Gate.WaitAsync(cancellationToken);
            gateTaken = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationAbort.Token);
            return await operation(Runtime, linked.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _scheduledOperations);
            if (gateTaken) { ResetAbortIfNeeded(); Gate.Release(); }
        }
    }

    public async Task RunExclusiveAsync(Func<PiSharp.Runtime.SessionRuntime, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _scheduledOperations);
        Touch();
        var gateTaken = false;
        try
        {
            await Gate.WaitAsync(cancellationToken);
            gateTaken = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationAbort.Token);
            await operation(Runtime, linked.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _scheduledOperations);
            if (gateTaken) { ResetAbortIfNeeded(); Gate.Release(); }
        }
    }

    /// <summary>Requests cancellation of the active or already-scheduled harness run without taking the operation gate, so abort can interrupt prompt/compaction.</summary>
    public void RequestAbort()
    {
        Touch();
        if (Volatile.Read(ref _scheduledOperations) > 0) _operationAbort.Cancel();
        Runtime.Harness.Abort();
    }

    public async Task<ServerSessionState> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var session = Runtime.Session;
        var harness = Runtime.Harness;
        var context = await session.BuildContextAsync(cancellationToken);
        return new ServerSessionState(
            Id,
            session.Metadata.Id,
            session.Metadata.Path,
            await session.GetSessionNameAsync(cancellationToken),
            session.Metadata.Cwd,
            harness.Model,
            harness.ThinkingLevel,
            harness.Phase == AgentHarnessPhase.Turn,
            harness.Phase == AgentHarnessPhase.Compaction,
            context.Messages.Count,
            EventLog.HeadSequence);
    }

    private void ResetAbortIfNeeded()
    {
        if (!_operationAbort.IsCancellationRequested) return;
        _operationAbort.Dispose();
        _operationAbort = new CancellationTokenSource();
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    private async Task OnRuntimeReboundAsync(PiSharp.Runtime.SessionRuntime runtime, CancellationToken cancellationToken)
    {
        BindCurrentHarness();
        var previous = _runtimeSessionId;
        var current = runtime.Session.Metadata.Id;
        if (!string.Equals(previous, current, StringComparison.Ordinal))
        {
            if (_runtimeSessionChanged is not null) await _runtimeSessionChanged(this, previous, current, cancellationToken);
            _runtimeSessionId = current;
        }
    }


    /// <summary>
    /// Subscribes to the runtime's shared extension registry so that an extension-emitted
    /// <c>advisor_note</c> (produced by the advisor plugin via <c>api.Events.EmitAsync</c>) is
    /// mapped onto the daemon per-session event lane through
    /// <see cref="AgentSessionEvent.FromAdvisor"/>. Uses <c>EmitEvent</c> so the flat event is
    /// retained in the log and broadcast to every attached client, exactly like the harness path.
    /// </summary>
    private void BindAdvisorEventForwarding()
    {
        _advisorSubscription?.Dispose();
        var extensionManager = Runtime.ExtensionManager;
        if (extensionManager is null) return;
        _advisorSubscription = extensionManager.Registry.RegisterHandler(
            "daemon:advisor",
            ExtensionEventNames.AdvisorNote,
            OnAdvisorEventAsync);
    }

    private Task OnAdvisorEventAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (evt.Payload is ExtensionAdvisorEvent advisorEvent)
        {
            EmitEvent(AgentSessionEvent.FromAdvisor(advisorEvent));
        }
        return Task.CompletedTask;
    }
    private void BindCurrentHarness()
    {
        _subscription?.Dispose();
        _subscription = Runtime.Harness.Subscribe((evt, _) =>
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var envelope = ServerEventEnvelope.FromFlat(Id, sequence, evt.ToFlat());
            EventLog.Append(envelope);
            foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryWrite(envelope);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Emits a server-generated flat event (e.g. a <c>ui_request</c>) into the retained event log
    /// and broadcasts it to all attached subscribers, mirroring the harness subscription path.
    /// </summary>
    public ServerEventEnvelope EmitEvent(AgentSessionEvent flatEvent)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var envelope = ServerEventEnvelope.FromFlat(Id, sequence, flatEvent);
        EventLog.Append(envelope);
        foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryWrite(envelope);
        return envelope;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _subscription?.Dispose();
        _advisorSubscription?.Dispose();
        RequestAbort();
        await Gate.WaitAsync();
        try { await Runtime.DisposeAsync(); }
        finally { Gate.Release(); }
        foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryComplete();
        Gate.Dispose();
        _operationAbort.Dispose();
        _lifetime.Dispose();
    }
}
