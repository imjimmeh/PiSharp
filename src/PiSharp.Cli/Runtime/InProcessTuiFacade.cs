using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Runtime;
using PiSharp.Tui.Interactive;

namespace PiSharp.Cli.Runtime;

/// <summary>
/// In-process <see cref="ITuiRuntimeFacade"/> backed by a live <see cref="SessionRuntime"/>.
/// Follows harness replacement on session rebind: resubscribes listeners to the new harness
/// and raises <see cref="OnHarnessReplaced"/> so the TUI can refresh session state.
/// </summary>
public sealed class InProcessTuiFacade : ITuiRuntimeFacade, IDisposable
{
    private readonly SessionRuntime _runtime;
    private readonly object _gate = new();
    private readonly List<Func<AgentHarnessEvent, CancellationToken, Task>> _listeners = [];
    private readonly List<IDisposable> _subscriptions = [];

    public InProcessTuiFacade(SessionRuntime runtime)
    {
        _runtime = runtime;
        _runtime.SetRebindSession((_, ct) =>
        {
            ResubscribeToCurrentHarness();
            return OnHarnessReplaced?.Invoke(ct) ?? Task.CompletedTask;
        });
    }

    public AgentHarnessPhase Phase => _runtime.Harness.Phase;
    public ModelDescriptor Model => _runtime.Harness.Model;
    public ThinkingLevel ThinkingLevel => _runtime.Harness.ThinkingLevel;
    public IReadOnlyList<string> ActiveToolNames => _runtime.Harness.ActiveToolNames;

    public Func<CancellationToken, Task>? OnHarnessReplaced { get; set; }

    public IDisposable Subscribe(Func<AgentHarnessEvent, CancellationToken, Task> listener)
    {
        lock (_gate)
        {
            _listeners.Add(listener);
            var subscription = _runtime.Harness.Subscribe(listener);
            _subscriptions.Add(subscription);
            return new Unsubscriber(this, listener, subscription);
        }
    }

    public void Abort() => _runtime.Harness.Abort();

    public Task PromptAsync(string text, IReadOnlyList<ImageContent> images, CancellationToken token)
        => _runtime.Harness.PromptAsync(text, images, token);

    public void Steer(AgentMessage message) => _runtime.Harness.Steer(message);

    public void Dispose()
    {
        _runtime.SetRebindSession((_, _) => Task.CompletedTask);
        lock (_gate)
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            _listeners.Clear();
        }
    }

    private void ResubscribeToCurrentHarness()
    {
        lock (_gate)
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            foreach (var listener in _listeners)
            {
                _subscriptions.Add(_runtime.Harness.Subscribe(listener));
            }
        }
    }

    private sealed class Unsubscriber(InProcessTuiFacade owner, Func<AgentHarnessEvent, CancellationToken, Task> listener, IDisposable subscription) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            subscription.Dispose();
            lock (owner._gate)
            {
                owner._subscriptions.Remove(subscription);
                owner._listeners.Remove(listener);
            }
        }
    }
}
