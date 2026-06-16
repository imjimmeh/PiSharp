using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Extensions;

public sealed class ExtensionEventBus : IExtensionEventBus, IDisposable
{
    private readonly ExtensionRegistry _registry;
    private readonly string _sourceId;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Exception> _diagnostics = [];
    private readonly Func<string, object?, CancellationToken, Task>? _emitBridge;
    private readonly ILogger _logger;

    public ExtensionEventBus(ExtensionRegistry registry, string sourceId, Func<string, object?, CancellationToken, Task>? emitBridge = null, ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _sourceId = sourceId;
        _emitBridge = emitBridge;
        _logger = loggerFactory?.CreateLogger<ExtensionEventBus>() ?? NullLogger<ExtensionEventBus>.Instance;
    }

    public IReadOnlyList<Exception> Diagnostics => _diagnostics;

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        var sub = _registry.RegisterHandler(_sourceId, eventName, handler);
        _subscriptions.Add(sub);
        return sub;
    }

    public async Task EmitAsync(string eventName, object payload, CancellationToken cancellationToken = default)
    {
        var handlers = _registry.HandlersFor(eventName);
        foreach (var registration in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var evt = new ExtensionEvent(eventName, null!, payload);
                await registration.Value.Handler(evt, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extension event handler for {EventName} failed", eventName);
                _diagnostics.Add(ex);
            }
        }
        if (_emitBridge is not null)
        {
            try
            {
                await _emitBridge(eventName, payload, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extension event handler for {EventName} failed", eventName);
                _diagnostics.Add(ex);
            }
        }
    }

    public void Clear()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
    }

    public void Dispose()
    {
        Clear();
    }
}
