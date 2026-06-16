using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

/// <summary>
/// Detects and strips terminal bracketed-paste markers (<c>\e[200~</c> / <c>\e[201~</c>)
/// so that multi-line clipboard content is inserted as text rather than submitting.
///
/// Terminal.Gui 2.0's <see cref="Terminal.Gui.TextView"/> does not support bracketed paste
/// natively; escape markers arrive as individual key events that must be accumulated here.
/// </summary>
internal sealed class PromptEditorPasteHandler
{
    private const string BracketedPasteStart = "\u001b[200~";
    private const string BracketedPasteEnd = "\u001b[201~";
    private string? _pendingBracketedPaste;
    private bool _isBracketedPasteActive;

    public bool IsPending => _pendingBracketedPaste is not null;

    public bool TryHandleEscapeKey(Key key)
    {
        if (key.IsCtrl || key.IsAlt || key.KeyCode != KeyCode.Esc) return false;

        _pendingBracketedPaste = _pendingBracketedPaste is null ? "\u001b" : _pendingBracketedPaste + "\u001b";
        return true;
    }

    public bool TryHandleMarkerKey(Key key, out string? textToInsert)
    {
        textToInsert = null;
        if (key.IsCtrl || key.IsAlt || key.KeyCode != KeyCode.Insert) return false;

        if (_isBracketedPasteActive)
        {
            textToInsert = _pendingBracketedPaste ?? string.Empty;
            _pendingBracketedPaste = null;
            _isBracketedPasteActive = false;
        }
        else
        {
            _pendingBracketedPaste = string.Empty;
            _isBracketedPasteActive = true;
        }

        return true;
    }

    public bool TryHandleText(string text, out string? textToInsert)
    {
        textToInsert = null;
        if (string.IsNullOrEmpty(text)) return _pendingBracketedPaste is not null;

        if (_isBracketedPasteActive)
        {
            var currentPaste = (_pendingBracketedPaste ?? string.Empty) + text;
            var endMarker = currentPaste.IndexOf(BracketedPasteEnd, StringComparison.Ordinal);
            if (endMarker < 0)
            {
                _pendingBracketedPaste = currentPaste;
                return true;
            }

            textToInsert = currentPaste[..endMarker];
            _pendingBracketedPaste = null;
            _isBracketedPasteActive = false;
            return true;
        }

        var current = (_pendingBracketedPaste ?? string.Empty) + text;
        if (_pendingBracketedPaste is not null && !BracketedPasteStart.StartsWith(current, StringComparison.Ordinal) && !current.StartsWith(BracketedPasteStart, StringComparison.Ordinal))
        {
            _pendingBracketedPaste = null;
            return false;
        }

        var start = _pendingBracketedPaste is null ? current.IndexOf(BracketedPasteStart, StringComparison.Ordinal) : 0;
        if (start < 0) return false;

        if (current.Length < start + BracketedPasteStart.Length)
        {
            _pendingBracketedPaste = current;
            return true;
        }

        var contentStart = start + BracketedPasteStart.Length;
        var end = current.IndexOf(BracketedPasteEnd, contentStart, StringComparison.Ordinal);
        if (end < 0)
        {
            _pendingBracketedPaste = current[contentStart..];
            _isBracketedPasteActive = true;
            return true;
        }

        textToInsert = current[contentStart..end];
        _pendingBracketedPaste = null;
        _isBracketedPasteActive = false;
        return true;
    }
}
