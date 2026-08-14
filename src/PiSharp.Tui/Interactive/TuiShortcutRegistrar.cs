using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Tui.Interactive.Keybindings;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed record TuiExtensionShortcutBinding(
    string SourceId,
    string Keys,
    string Description,
    IReadOnlyList<Key> TerminalKeys,
    Func<CancellationToken, Task> InvokeAsync);

public static class TuiShortcutRegistrar
{
    public static ILoggerFactory? LoggerFactory { get; set; }

    private static ILogger Logger => LoggerFactory?.CreateLogger(nameof(TuiShortcutRegistrar)) ?? NullLogger.Instance;

    /// <summary>
    /// Process-wide effective binding table. <c>TuiHost</c> replaces this with the store it loads
    /// from the user keybindings file, so static shortcut dispatch honors remaps. Consumers that
    /// need a specific store use the store-accepting overloads.
    /// </summary>
    public static TuiKeybindingStore DefaultStore
    {
        get => TuiKeybindings.DefaultStore;
        set => TuiKeybindings.DefaultStore = value;
    }

    public static bool TryResolveGlobalAction(Key key, out TuiShortcutAction action)
        => TryResolveGlobalAction(key, DefaultStore, out action);

    public static bool TryResolveGlobalAction(Key key, TuiKeybindingStore store, out TuiShortcutAction action)
        => store.TryResolveGlobalAction(key, out action);

    public static bool TryResolveExtensionShortcut(
        Key key,
        IReadOnlyList<TuiExtensionShortcutBinding> extensionShortcuts,
        Action<string> reportError,
        out TuiExtensionShortcutBinding binding,
        TuiKeybindingStore? store = null)
    {
        binding = default!;
        if (TryResolveGlobalAction(key, store ?? DefaultStore, out var builtInAction))
        {
            var conflicting = extensionShortcuts
                .Where(shortcut => shortcut.TerminalKeys.Contains(key))
                .Select(shortcut => shortcut.SourceId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (conflicting.Length > 0)
                reportError($"Extension shortcut '{key}' conflicts with built-in shortcut '{builtInAction}' and was ignored: {string.Join(", ", conflicting)}.");
            return false;
        }

        var matches = extensionShortcuts
            .Where(shortcut => shortcut.TerminalKeys.Contains(key))
            .OrderBy(shortcut => shortcut.SourceId, StringComparer.Ordinal)
            .ThenBy(shortcut => shortcut.Keys, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length == 0) return false;

        if (matches.Length > 1)
        {
            reportError($"Extension shortcut conflict for '{key}': {string.Join(", ", matches.Select(match => match.SourceId))}. No handler was invoked.");
            return false;
        }

        binding = matches[0];
        return true;
    }

    public static bool TryDispatchExtensionShortcut(
        Key key,
        IReadOnlyList<TuiExtensionShortcutBinding> extensionShortcuts,
        Action<string> reportError,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveExtensionShortcut(key, extensionShortcuts, reportError, out var binding)) return false;

        _ = InvokeExtensionShortcutAsync(binding, reportError, cancellationToken);
        return true;
    }

    public static bool TryDispatchShortcutKey(
        Key key,
        TuiShortcutDispatcher dispatcher,
        TuiShortcutContext context,
        Func<IReadOnlyList<TuiExtensionShortcutBinding>> getExtensionShortcuts,
        Action<string> reportError,
        CancellationToken cancellationToken = default,
        bool allowHandledGlobalShortcuts = false,
        TuiKeybindingStore? store = null)
    {
        if (TryResolveGlobalAction(key, store ?? DefaultStore, out var action))
        {
            Logger.LogDebug(
                "Resolved TUI shortcut key {Key} handled={Handled} allowHandledGlobal={AllowHandledGlobalShortcuts} action={Action}",
                DescribeKey(key), key.Handled, allowHandledGlobalShortcuts, action);

            if (key.Handled && !allowHandledGlobalShortcuts)
            {
                Logger.LogDebug("Skipped TUI shortcut key {Key} because it was already handled", DescribeKey(key));
                return false;
            }

            if (!dispatcher.TryDispatch(action, context))
            {
                Logger.LogDebug("No dispatcher command registered for TUI shortcut action {Action}", action);
                return false;
            }

            key.Handled = true;
            Logger.LogDebug("Dispatched TUI shortcut key {Key} action={Action}", DescribeKey(key), action);
            return true;
        }

        if (key.Handled)
        {
            Logger.LogDebug("Skipped handled non-global TUI shortcut key {Key}", DescribeKey(key));
            return false;
        }

        if (!TryDispatchExtensionShortcut(key, getExtensionShortcuts(), reportError, cancellationToken))
        {
            if (ShouldLogKey(key)) Logger.LogDebug("No TUI shortcut matched key {Key}", DescribeKey(key));
            return false;
        }
        key.Handled = true;
        return true;
    }

    private static async Task InvokeExtensionShortcutAsync(TuiExtensionShortcutBinding binding, Action<string> reportError, CancellationToken cancellationToken)
    {
        try
        {
            await binding.InvokeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Extension shortcut {Keys} from {SourceId} failed", binding.Keys, binding.SourceId);
            reportError($"Extension shortcut '{binding.Keys}' from {binding.SourceId} failed: {ex.Message}");
        }
    }

    private static string DescribeKey(Key key)
        => $"KeyCode={key.KeyCode}, Ctrl={key.IsCtrl}, Alt={key.IsAlt}, Shift={key.IsShift}, Handled={key.Handled}";

    private static bool ShouldLogKey(Key key)
    {
        var code = key.KeyCode & KeyCode.CharMask;
        return key.IsCtrl || key.IsAlt || (int)code is > 0 and <= 31 || code == KeyCode.C;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            dispose();
        }
    }
}
