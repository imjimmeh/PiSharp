using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Input;

internal sealed class TuiInputRouter : IDisposable
{
    private readonly ITuiApplicationContext _appContext;
    private readonly Func<ITuiInputCapture?> _getActiveCapture;
    private readonly Func<Key, bool> _tryHandleHostInput;
    private readonly Func<Key, bool> _tryDispatchShortcut;
    private readonly ILogger<TuiInputRouter> _logger;
    private bool _disposed;
    private bool _attached;

    public TuiInputRouter(
        ITuiApplicationContext appContext,
        Func<ITuiInputCapture?> getActiveCapture,
        Func<Key, bool> tryHandleHostInput,
        Func<Key, bool> tryDispatchShortcut,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(appContext);
        ArgumentNullException.ThrowIfNull(getActiveCapture);
        ArgumentNullException.ThrowIfNull(tryHandleHostInput);
        ArgumentNullException.ThrowIfNull(tryDispatchShortcut);

        _appContext = appContext;
        _getActiveCapture = getActiveCapture;
        _tryHandleHostInput = tryHandleHostInput;
        _tryDispatchShortcut = tryDispatchShortcut;
        _logger = loggerFactory?.CreateLogger<TuiInputRouter>() ?? NullLogger<TuiInputRouter>.Instance;
    }

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached) return;
        _attached = true;
        _appContext.KeyDown += HandleKey;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached)
        {
            _appContext.KeyDown -= HandleKey;
            _attached = false;
        }
    }

    internal void HandleKeyForTest(Key key)
    {
        HandleKey(this, key);
    }

    private void HandleKey(object? sender, Key key)
    {
        var capture = _getActiveCapture();
        var wasHandled = key.Handled;
        var isReusableHandledInput = wasHandled && IsReusableHandledInput(key);

        if (capture is not null)
        {
            _logger.LogDebug(
                "TUI input router received key {Key} activeCapture={CaptureName} wasHandled={WasHandled} reusableHandled={ReusableHandled}",
                DescribeKey(key), capture.Name, wasHandled, isReusableHandledInput);
        }

        if (key.Handled)
        {
            if (capture is null && !isReusableHandledInput)
            {
                _logger.LogDebug("TUI input router ignored already-handled key {Key} because no capture was active", DescribeKey(key));
                return;
            }

            key.Handled = false;
        }

        if (capture is not null)
        {
            var handledByCapture = capture.TryHandleKey(key);
            _logger.LogDebug(
                "TUI input router capture processed key {Key} activeCapture={CaptureName} handled={Handled}",
                DescribeKey(key), capture.Name, handledByCapture);

            if (handledByCapture)
            {
                key.Handled = true;
                return;
            }
        }

        if (wasHandled && !isReusableHandledInput)
        {
            key.Handled = true;
            _logger.LogDebug("TUI input router restored handled state after capture declined key {Key}", DescribeKey(key));
            return;
        }

        if (_tryHandleHostInput(key))
        {
            key.Handled = true;
            return;
        }

        if (_tryDispatchShortcut(key))
        {
            key.Handled = true;
            return;
        }
    }

    private static bool IsReusableHandledInput(Key key)
        => IsTranscriptNavigationKey(key) || IsHandledShiftTab(key) || IsSidebarToggleKey(key);

    // The Windows terminal driver reuses Key instances across key-repeat events. After
    // the first F6/F7 dispatch sets key.Handled = true, the cached instance is reused for
    // all subsequent driver events, arriving here with Handled already true. These keys
    // must be re-dispatched on every event so the sidebar toggles consistently.
    private static bool IsSidebarToggleKey(Key key)
        => !key.IsCtrl && !key.IsAlt && !key.IsShift
            && (key.KeyCode == KeyCode.F6 || key.KeyCode == KeyCode.F7);

    private static bool IsTranscriptNavigationKey(Key key)
    {
        if (key.IsAlt) return false;

        var baseKeyCode = BaseKeyCode(key);
        return baseKeyCode is KeyCode.CursorUp or KeyCode.CursorDown or KeyCode.PageUp or KeyCode.PageDown or KeyCode.Home or KeyCode.End;
    }

    private static bool IsHandledShiftTab(Key key)
    {
        var baseKeyCode = BaseKeyCode(key);
        return key.IsShift && !key.IsCtrl && !key.IsAlt && baseKeyCode == KeyCode.Tab;
    }

    private static KeyCode BaseKeyCode(Key key)
        => key.KeyCode & ~(KeyCode.ShiftMask | KeyCode.CtrlMask | KeyCode.AltMask);

    private static string DescribeKey(Key key)
        => $"KeyCode={key.KeyCode}, Ctrl={key.IsCtrl}, Alt={key.IsAlt}, Shift={key.IsShift}, Handled={key.Handled}";
}
