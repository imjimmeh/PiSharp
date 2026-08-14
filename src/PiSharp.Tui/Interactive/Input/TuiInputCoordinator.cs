using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Input;

internal sealed class TuiInputCoordinator(
    Window window,
    ChatView chat,
    PromptEditor prompt,
    Action scheduleRender,
    TuiShortcutDispatcher shortcutDispatcher,
    TuiShortcutContext shortcutContext,
    TuiShortcutController shortcutController,
    Action<string> reportShortcutError,
    CancellationToken cancellationToken,
    Func<bool>? isInputCaptured = null,
    Func<bool>? isEditorComponentActive = null,
    ILoggerFactory? loggerFactory = null) : IDisposable
{
    private bool _disposed;
    private readonly ILogger<TuiInputCoordinator> _logger = loggerFactory?.CreateLogger<TuiInputCoordinator>() ?? NullLogger<TuiInputCoordinator>.Instance;

    public TuiInputCoordinator(
        Window window,
        ChatView chat,
        PromptEditor prompt,
        Action scheduleRender,
        TuiShortcutDispatcher shortcutDispatcher,
        TuiShortcutContext shortcutContext,
        TuiShortcutController shortcutController,
        Action<string> reportShortcutError)
        : this(window, chat, prompt, scheduleRender, shortcutDispatcher, shortcutContext, shortcutController, reportShortcutError, CancellationToken.None)
    {
    }

    public void Attach()
    {
        prompt.KeyDown += HandlePromptShortcutKeys;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        prompt.KeyDown -= HandlePromptShortcutKeys;
    }

    internal bool TryHandleHostInput(Key key)
    {
        if (key.Handled || Application.Top != window) return false;

        // While an editor-placement slot replaces the prompt, its focused TextView handles
        // navigational and printable keys natively; global shortcuts still dispatch afterwards
        // in the router. Skip all host-input policy so keys reach the editor.
        if (isEditorComponentActive?.Invoke() == true) return false;

        if (key.KeyCode == KeyCode.PageUp)
        {
            key.Handled = true;
            chat.ScrollPage(-1);
        }
        else if (key.KeyCode == KeyCode.PageDown)
        {
            key.Handled = true;
            chat.ScrollPage(1);
        }
        else if (key.KeyCode == KeyCode.Home && prompt.PromptText.Length == 0)
        {
            key.Handled = true;
            chat.ScrollToStart();
        }
        else if (key.KeyCode == KeyCode.End)
        {
            if (prompt.PromptText.Length > 0) return false;

            key.Handled = true;
            chat.ScrollToEnd();
        }
        else if (key.IsCtrl && key.KeyCode == KeyCode.CursorUp)
        {
            key.Handled = true;
            chat.ScrollLine(-1);
        }
        else if (key.IsCtrl && key.KeyCode == KeyCode.CursorDown)
        {
            key.Handled = true;
            chat.ScrollLine(1);
        }

        if (key.Handled)
        {
            prompt.FocusAtEnd();
            scheduleRender();
            return true;
        }

        if (prompt.HasFocus) return false;
        if (isInputCaptured?.Invoke() == true) return false;
        if (key.IsAlt) return false;

        if (!key.IsCtrl && key.KeyCode is KeyCode.CursorUp or KeyCode.CursorDown)
        {
            key.Handled = true;
            chat.ScrollLine(key.KeyCode == KeyCode.CursorUp ? -1 : 1);
            prompt.FocusAtEnd();
            scheduleRender();
            return true;
        }

        if (key.IsCtrl) return false;

        if (!TuiKeyText.TryGetPrintableText(key, out var text)) return false;

        key.Handled = true;
        prompt.InsertAtEnd(text);
        scheduleRender();
        return true;
    }

    private void HandlePromptShortcutKeys(object? _, Key key)
    {
        if (Application.Top != window) return;
        if (ShouldLogKey(key))
            _logger.LogDebug("Prompt shortcut dispatch received key {Key}", DescribeKey(key));

        var dispatched = TuiShortcutRegistrar.TryDispatchShortcutKey(
            key,
            shortcutDispatcher,
            shortcutContext,
            shortcutController.BuildExtensionShortcutBindings,
            reportShortcutError,
            cancellationToken,
            true);

        if (ShouldLogKey(key))
            _logger.LogDebug("Prompt shortcut dispatch completed key {Key} dispatched={Dispatched}", DescribeKey(key), dispatched);
    }

    private static bool ShouldLogKey(Key key)
    {
        var code = (int)(key.KeyCode & KeyCode.CharMask);
        return key.IsCtrl || key.IsAlt || code is > 0 and <= 31 || (key.KeyCode & KeyCode.CharMask) == KeyCode.C;
    }

    private static string DescribeKey(Key key)
        => $"KeyCode={key.KeyCode}, Ctrl={key.IsCtrl}, Alt={key.IsAlt}, Shift={key.IsShift}, Handled={key.Handled}";
}
