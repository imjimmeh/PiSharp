using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Extensions;

public interface IExtensionRegistryChangeStream
{
    IDisposable Subscribe(Func<ExtensionRegistryChange, CancellationToken, Task> handler);
    Task PublishAsync(ExtensionRegistryChange change, CancellationToken cancellationToken = default);
}

public sealed record ExtensionRegistryChangeDeliveryFailure(ExtensionRegistryChange Change, Exception Exception);

public sealed class ExtensionRegistryChangeStream : IExtensionRegistryChangeStream
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly List<ExtensionRegistryChangeDeliveryFailure> _failures = [];
    private readonly ILogger _logger;

    public ExtensionRegistryChangeStream(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<ExtensionRegistryChangeStream>() ?? NullLogger<ExtensionRegistryChangeStream>.Instance;
    }

    public IReadOnlyList<ExtensionRegistryChangeDeliveryFailure> Failures
    {
        get { lock (_gate) return _failures.ToArray(); }
    }

    public IDisposable Subscribe(Func<ExtensionRegistryChange, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, handler);
        lock (_gate) _subscriptions.Add(subscription);
        return subscription;
    }

    public async Task PublishAsync(ExtensionRegistryChange change, CancellationToken cancellationToken = default)
    {
        foreach (var subscription in Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await subscription.Handler(change, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Extension registry change delivery failed");
                lock (_gate) _failures.Add(new ExtensionRegistryChangeDeliveryFailure(change, exception));
            }
        }
    }

    private IReadOnlyList<Subscription> Snapshot()
    {
        lock (_gate) return _subscriptions.ToArray();
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate) _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(ExtensionRegistryChangeStream owner, Func<ExtensionRegistryChange, CancellationToken, Task> handler) : IDisposable
    {
        private int _disposed;
        public Func<ExtensionRegistryChange, CancellationToken, Task> Handler => handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(this);
        }
    }
}
