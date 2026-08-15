using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Extensions;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed record TuiShortcutControllerOptions(
    Func<IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>>> GetExtensionShortcuts,
    IExtensionUi ExtensionUi,
    Action<string> ReportError)
{
    public ILogger<TuiShortcutController> Logger { get; init; } = NullLogger<TuiShortcutController>.Instance;
}

public sealed class TuiShortcutController(TuiShortcutControllerOptions options)
{
    private readonly ILogger<TuiShortcutController> _logger = options.Logger;
    private readonly object _gate = new();
    private IReadOnlyList<TuiExtensionShortcutBinding>? _cachedBindings;

    /// <summary>
    /// Key-path accessor: returns the last asynchronously rebuilt bindings, or an empty list until
    /// the first <see cref="RefreshExtensionShortcutsAsync"/> lands. It never consults the
    /// (potentially remote) shortcut source synchronously, so the UI thread can never block on a
    /// WebSocket round trip while dispatching a keystroke.
    /// </summary>
    public IReadOnlyList<TuiExtensionShortcutBinding> BuildExtensionShortcutBindings()
    {
        lock (_gate) return _cachedBindings ?? [];
    }

    /// <summary>Clears the cached bindings so the key path returns empty until the next refresh.</summary>
    public void InvalidateExtensionShortcuts()
    {
        lock (_gate) _cachedBindings = null;
    }

    /// <summary>
    /// Rebuilds the bindings by reading the shortcut source off the caller's thread (the caller is
    /// typically the UI thread) and atomically swapping them into the cache. A slow or unavailable
    /// source delays only the background refresh — the key path keeps returning the last good cache
    /// (or empty) and never blocks. Reportable build errors are surfaced; other failures leave the
    /// previous cache intact.
    /// </summary>
    public async Task RefreshExtensionShortcutsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Extension shortcuts refresh started");
        IReadOnlyList<TuiExtensionShortcutBinding> bindings;
        try
        {
            // Task.Run moves the blocking source read off the caller's thread; ConfigureAwait(false)
            // keeps the continuation (and the cache swap) on a pool thread so nothing here ever
            // depends on a UI main loop that might be wedged.
            bindings = await Task.Run(() => BuildCore(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to refresh extension shortcuts");
            return;
        }

        lock (_gate) _cachedBindings = bindings;
        _logger.LogDebug("Extension shortcuts refresh completed bindings={BindingCount}", bindings.Count);
    }

    private IReadOnlyList<TuiExtensionShortcutBinding> BuildCore()
    {
        var registrations = options.GetExtensionShortcuts();
        var bindings = new List<TuiExtensionShortcutBinding>();
        foreach (var registration in registrations)
        {
            if (!TuiShortcutKeyParser.TryParse(registration.Value.Keys, out var terminalKeys))
            {
                _logger.LogDebug("Skipping extension shortcut with invalid keys sourceId={SourceId} keys={Keys}", registration.SourceId, registration.Value.Keys);
                options.ReportError($"Invalid extension shortcut '{registration.Value.Keys}' from {registration.SourceId}: unsupported key string.");
                continue;
            }

            var conflictingAction = terminalKeys
                .Select(key => TuiShortcutRegistrar.TryResolveGlobalAction(key, out var action) ? action : (TuiShortcutAction?)null)
                .FirstOrDefault(action => action is not null);
            if (conflictingAction is not null)
            {
                _logger.LogDebug("Skipping extension shortcut conflicting with built-in action sourceId={SourceId} keys={Keys} action={Action}", registration.SourceId, registration.Value.Keys, conflictingAction);
                options.ReportError($"Extension shortcut '{registration.Value.Keys}' from {registration.SourceId} conflicts with built-in shortcut '{conflictingAction}' and was ignored.");
                continue;
            }

            bindings.Add(new TuiExtensionShortcutBinding(
                registration.SourceId,
                registration.Value.Keys,
                registration.Value.Description,
                terminalKeys,
                token => registration.Value.InvokeAsync(new ExtensionCommandContext(
                    registration.Value.Keys,
                    string.Empty,
                    options.ExtensionUi,
                    UnavailableExtensionSessionApi.Instance,
                    UnavailableExtensionModelApi.Instance,
                    UnavailableExtensionToolApi.Instance,
                    new Dictionary<string, object?>(),
                    token), token)));
        }

        return bindings;
    }

    public bool TryDispatchShortcutKey(
        Key key,
        TuiShortcutDispatcher dispatcher,
        TuiShortcutContext context,
        CancellationToken cancellationToken = default)
        => TuiShortcutRegistrar.TryDispatchShortcutKey(
            key,
            dispatcher,
            context,
            BuildExtensionShortcutBindings,
            options.ReportError,
            cancellationToken);
}
