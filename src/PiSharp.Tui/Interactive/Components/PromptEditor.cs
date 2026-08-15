using System.Drawing;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public sealed class PromptEditor : TextView, IPromptEditorSurface
{
    private const Command PasteCommand = Command.Paste;
    private readonly ILogger<PromptEditor> _logger;
    private readonly PromptEditorController _controller;
    private readonly PromptEditorKeyMap _keyMap;
    private readonly PromptEditorPasteHandler _pasteHandler = new();
    private string _pendingUnmarkedPasteText = string.Empty;
    private int _promptCursorOffset;
    private bool _isApplyingControllerText;

    public PromptEditor(PromptEditorKeyMap? keyMap = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PromptEditor>() ?? NullLogger<PromptEditor>.Instance;
        _controller = new PromptEditorController(this);
        _keyMap = keyMap ?? PromptEditorKeyMap.CreateDefault();
        WordWrap = true;
        Multiline = true;
        CanFocus = true;
        CursorVisibility = CursorVisibility.Default;
        WantMousePositionReports = true;
        RegisterEditorCommands();

        KeyDown += (_, key) =>
        {
            if (TryDismissSuggestions(key) || TryHandleBracketedPasteKey(key) || TryHandleClipboardPasteKey(key) || TryRequestTranscriptScroll(key) || TryHandlePromptPolicyInput(key) || _keyMap.TryHandle(key, _controller)) key.Handled = true;

            if (!key.Handled && (key.IsCtrl || key.IsAlt)) key.Handled = true;
        };
        TextChanged += (_, _) =>
        {
            if (_isApplyingControllerText) return;

            _controller.HandleTextEdited();
        };
        ContentsChanged += (_, _) =>
        {
            if (_isApplyingControllerText) return;

            _controller.HandleTextEdited();
        };
        UnwrappedCursorPosition += (_, position) => TrackCursorOffset(OffsetFromUnwrappedPosition(position));
    }

    public event Func<string, CancellationToken, Task>? Submitted
    {
        add => _controller.Submitted += value;
        remove => _controller.Submitted -= value;
    }

    public event Action? SuggestionsChanged
    {
        add => _controller.SuggestionsChanged += value;
        remove => _controller.SuggestionsChanged -= value;
    }

    public event Action<int>? TranscriptScrollRequested;

    public Func<string, int, IReadOnlyList<PromptCompletion>>? Complete
    {
        get => _controller.Complete;
        set => _controller.Complete = value;
    }

    public Func<string, int, CancellationToken, Task<IReadOnlyList<PromptCompletion>>>? CompleteAsync
    {
        get => _controller.CompleteAsync;
        set => _controller.CompleteAsync = value;
    }

    internal Action<Action>? PostSuggestionUpdate
    {
        get => _controller.PostSuggestionUpdate;
        set => _controller.PostSuggestionUpdate = value;
    }

    internal TimeSpan AsyncCompletionDebounceDelay
    {
        get => _controller.AsyncCompletionDebounceDelay;
        set => _controller.AsyncCompletionDebounceDelay = value;
    }

    public Func<string, IReadOnlyList<string>>? CompleteText
    {
        set => _controller.CompleteText = value;
    }

    public bool AllowEmptySubmit
    {
        get => _controller.AllowEmptySubmit;
        set => _controller.AllowEmptySubmit = value;
    }

    public string AcceptedSuggestionSuffix
    {
        get => _controller.AcceptedSuggestionSuffix;
        set => _controller.AcceptedSuggestionSuffix = value;
    }

    public bool IsSubmitting => _controller.IsSubmitting;
    public IReadOnlyList<PromptCompletion> Completions => _controller.Completions;
    public IReadOnlyList<string> Suggestions => _controller.Suggestions;
    public int SelectedSuggestionIndex => _controller.SelectedSuggestionIndex;
    public string? SelectedSuggestion => _controller.SelectedSuggestion;
    public string PromptText => PromptTextBuffer.Normalize(Text?.ToString() ?? string.Empty);
    public int CursorOffset
    {
        get => _promptCursorOffset;
    }
    public IReadOnlyList<string> PromptHistory => _controller.PromptHistory;

    public void SetPromptText(string text) => _controller.SetPromptText(text);

    public void FocusAtEnd()
    {
        if (!HasFocus) SetFocus();
        MoveEnd();
    }

    public new void MoveEnd()
    {
        ApplyText(PromptText, PromptText.Length);
    }

    public new void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var normalizedText = PromptTextBuffer.Normalize(text);
        var cursorOffset = CursorOffset;
        InsertTextWithTextView(normalizedText);
        TrackCursorOffset(cursorOffset + normalizedText.Length);
    }

    public void InsertAtEnd(string text) => _controller.InsertAtEnd(text);

    public void RecordSubmittedPrompt(string text) => _controller.RecordSubmittedPrompt(text);

    public bool MovePromptHistory(int delta) => _controller.MovePromptHistory(delta);

    public Task SubmitAsync(CancellationToken cancellationToken = default) => _controller.SubmitAsync(cancellationToken);

    public void ClearPrompt() => _controller.ClearEditor();

    public void RefreshSuggestions() => _controller.RefreshSuggestions();

    public void DismissSuggestions() => _controller.TryDismissSuggestions();

    public void AcceptFirstSuggestion() => _controller.AcceptFirstSuggestion();

    public void MoveSuggestionSelection(int delta) => _controller.MoveSuggestionSelection(delta);

    void IPromptEditorSurface.SetPromptText(string text) => SetPromptTextCore(text);

    void IPromptEditorSurface.SetCursorOffset(int offset)
    {
        ApplyText(PromptText, offset);
    }

    void IPromptEditorSurface.InsertAtEnd(string text)
    {
        MoveEnd();
        InsertText(text);
    }

    bool IPromptEditorSurface.MoveCursorVertical(int delta) => false;

    private void SetPromptTextCore(string text)
    {
        text = PromptTextBuffer.Normalize(text);
        ApplyText(text, text.Length);
    }

    protected override bool OnMouseEvent(MouseEventArgs args)
    {
        if (TryRequestTranscriptScroll(args)) return true;

        var handled = base.OnMouseEvent(args);
        if (IsPrimaryButtonCursorEvent(args.Flags))
        {
            _controller.RefreshSuggestions();
        }

        return handled;
    }

    protected override bool OnKeyDownNotHandled(Key key)
    {
        if (base.OnKeyDownNotHandled(key)) return true;

        // Headless tests synthesize keys without AsGrapheme; production keyboard input is handled by TextView above.
        if (!TuiKeyText.TryGetPrintableText(key, out var printableText)) return false;

        InsertText(printableText);
        return true;
    }

    public override void OnUnwrappedCursorPosition(int? cRow = null, int? cCol = null)
    {
        base.OnUnwrappedCursorPosition(cRow, cCol);
        if (cRow is null || cCol is null) return;

        TrackCursorOffset(OffsetFromUnwrappedPosition(new Point(cCol.Value, cRow.Value)));
        if (!_isApplyingControllerText) _controller.RefreshSuggestions();
    }

    protected override bool OnMouseWheel(MouseEventArgs args)
        => TryRequestTranscriptScroll(args) || base.OnMouseWheel(args);

    private static bool IsPrimaryButtonCursorEvent(MouseFlags flags)
        => flags.HasFlag(MouseFlags.Button1Clicked)
            || flags.HasFlag(MouseFlags.Button1Pressed);

    private bool TryRequestTranscriptScroll(MouseEventArgs args)
    {
        if (!args.IsWheel) return false;

        if (args.Flags.HasFlag(MouseFlags.WheeledUp)) TranscriptScrollRequested?.Invoke(-1);
        else if (args.Flags.HasFlag(MouseFlags.WheeledDown)) TranscriptScrollRequested?.Invoke(1);
        else return false;

        args.Handled = true;
        return true;
    }

    private bool TryDismissSuggestions(Key key)
    {
        if (key.IsCtrl || key.IsAlt || key.KeyCode != KeyCode.Esc) return false;
        return _controller.TryDismissSuggestions();
    }

    private bool TryRequestTranscriptScroll(Key key)
    {
        if (TranscriptScrollRequested is null) return false;
        // While an inline selection is active the arrow keys belong to the picker, not the
        // transcript. Without this guard, arrows are hijacked into transcript scrolling during
        // the brief window before async suggestions have loaded (Suggestions.Count == 0).
        if (_controller.AllowEmptySubmit) return false;
        if (key.IsCtrl || key.IsAlt) return false;
        if (key.KeyCode is not (KeyCode.CursorUp or KeyCode.CursorDown)) return false;
        if (Suggestions.Count > 0) return false;
        if (!string.IsNullOrWhiteSpace(PromptText)) return false;

        TranscriptScrollRequested(key.KeyCode == KeyCode.CursorUp ? -1 : 1);
        return true;
    }

    private bool TryHandlePromptPolicyInput(Key key)
    {
        if (TryConsumePendingUnmarkedPasteText(key)) return true;

        if (TryHandleBracketedPasteMarkerKey(key)) return true;

        if (_pasteHandler.IsPending && TryGetBracketedPasteCharacter(key, out var pasteCharacter)) return TryHandleBracketedPasteText(pasteCharacter);

        if (_pasteHandler.IsPending && (key.KeyCode == KeyCode.Enter || (key.KeyCode & KeyCode.CharMask) == KeyCode.Enter)) return TryHandleBracketedPasteText("\n");

        if (_pasteHandler.IsPending) return true;

        if ((key.KeyCode & KeyCode.CharMask) == KeyCode.Enter && (key.IsShift || key.IsCtrl))
        {
            InsertText("\n");
            return true;
        }

        if (IsPlainEnterKey(key) && TryCompleteUnmarkedClipboardPaste()) return true;

        if (key.IsCtrl || key.IsAlt) return false;

        return false;
    }

    private bool TryCompleteUnmarkedClipboardPaste()
    {
        if (!TryGetClipboardText(out var clipboardText)) return false;

        clipboardText = PromptTextBuffer.Normalize(clipboardText);
        var firstLineBreak = clipboardText.IndexOf('\n');
        if (firstLineBreak <= 0) return false;

        var pastedPrefix = clipboardText[..firstLineBreak];
        var textBeforeCursor = PromptText[..CursorOffset];
        if (!textBeforeCursor.EndsWith(pastedPrefix, StringComparison.Ordinal)) return false;

        InsertText(clipboardText[pastedPrefix.Length..]);
        _pendingUnmarkedPasteText = clipboardText[(pastedPrefix.Length + 1)..];
        return true;
    }

    private bool TryConsumePendingUnmarkedPasteText(Key key)
    {
        if (_pendingUnmarkedPasteText.Length == 0) return false;
        if (!TryGetBracketedPasteCharacter(key, out var text))
        {
            _pendingUnmarkedPasteText = string.Empty;
            return false;
        }

        text = PromptTextBuffer.Normalize(text);
        if (!_pendingUnmarkedPasteText.StartsWith(text, StringComparison.Ordinal))
        {
            _pendingUnmarkedPasteText = string.Empty;
            return false;
        }

        _pendingUnmarkedPasteText = _pendingUnmarkedPasteText[text.Length..];
        return true;
    }

    private static bool TryGetBracketedPasteCharacter(Key key, out string text)
    {
        if (TuiKeyText.TryGetPrintableText(key, out text)) return true;
        if (key.KeyCode == KeyCode.Enter || (key.KeyCode & KeyCode.CharMask) == KeyCode.Enter || key.AsRune.Value is '\n' or '\r')
        {
            text = "\n";
            return true;
        }

        return false;
    }

    private bool TryHandleBracketedPasteKey(Key key)
    {
        if (key.IsCtrl || key.IsAlt) return false;
        return _pasteHandler.TryHandleEscapeKey(key);
    }

    private bool TryHandleClipboardPasteKey(Key key)
    {
        if (!IsClipboardPasteKey(key)) return false;

        PasteClipboardText();
        return true;
    }

    private static bool IsClipboardPasteKey(Key key)
        => (key.IsCtrl && !key.IsAlt && (key.KeyCode & KeyCode.CharMask) == KeyCode.V)
            || (key.IsShift && !key.IsCtrl && !key.IsAlt && (key.KeyCode & KeyCode.CharMask) == KeyCode.Insert);

    private bool PasteClipboardText()
    {
        if (TryGetClipboardText(out var clipboardText))
        {
            InsertText(clipboardText);
            return true;
        }

        return true;
    }

    private static bool TryGetClipboardText(out string text)
    {
        if (Clipboard.TryGetClipboardData(out var osClipboardText) && !string.IsNullOrEmpty(osClipboardText))
        {
            text = osClipboardText;
            return true;
        }

        if (!string.IsNullOrEmpty(Clipboard.Contents))
        {
            text = Clipboard.Contents;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool IsPlainEnterKey(Key key)
        => !key.IsCtrl && !key.IsAlt && !key.IsShift && key.KeyCode == KeyCode.Enter;

    private bool TryHandleBracketedPasteMarkerKey(Key key)
    {
        if (key.IsCtrl || key.IsAlt || key.KeyCode != KeyCode.Insert) return false;

        if (!_pasteHandler.TryHandleMarkerKey(key, out var textToInsert)) return false;
        if (textToInsert is not null) InsertText(textToInsert);
        return true;
    }

    private bool TryHandleBracketedPasteText(string text)
    {
        if (!_pasteHandler.TryHandleText(text, out var textToInsert)) return false;
        if (textToInsert is not null) InsertText(textToInsert);
        return true;
    }

    private void ApplyText(string text, int cursorOffset)
    {
        text = PromptTextBuffer.Normalize(text);
        cursorOffset = Math.Clamp(cursorOffset, 0, text.Length);
        _isApplyingControllerText = true;
        try
        {
            Text = string.Empty;
            if (cursorOffset > 0) InsertTextWithTextView(text[..cursorOffset]);
            var cursorPosition = CursorPosition;
            if (cursorOffset < text.Length) InsertTextWithTextView(text[cursorOffset..]);
            if (cursorOffset < text.Length) CursorPosition = cursorPosition;

            TrackCursorOffset(cursorOffset);
        }
        finally
        {
            _isApplyingControllerText = false;
        }
    }

    private void TrackCursorOffset(int offset) => _promptCursorOffset = Math.Clamp(offset, 0, PromptText.Length);

    private void InsertTextWithTextView(string text)
    {
        var restoreWordWrap = WordWrap && text.Contains('\t');
        if (restoreWordWrap) WordWrap = false;

        try
        {
            InsertTextWithCurrentWordWrap(text);
        }
        finally
        {
            if (restoreWordWrap)
            {
                try
                {
                    WordWrap = true;
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    _logger.LogDebug(exception, "Terminal.Gui failed to rewrap inserted prompt text.");
                }
            }
        }
    }

    private void InsertTextWithCurrentWordWrap(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            if (WordWrap && rune.Value is '\n')
            {
                InvokeCommand(Command.NewLine);
            }
            else
            {
                base.InsertText(text.Substring(i, rune.Utf16SequenceLength));
            }

            i += rune.Utf16SequenceLength;
        }
    }

    private int OffsetFromUnwrappedPosition(Point position)
    {
        var text = PromptText;
        var offset = 0;
        var row = 0;
        while (row < position.Y && offset < text.Length)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0) return text.Length;
            offset = newline + 1;
            row++;
        }

        var lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        return Math.Min(offset + Math.Max(0, position.X), lineEnd);
    }

    private void RegisterEditorCommands()
    {
        AddCommand(PasteCommand, () => PasteClipboardText());

        KeyBindings.Remove(Key.Enter);
        KeyBindings.Remove(Key.Tab);
        KeyBindings.Remove(Key.Tab.WithShift);
        KeyBindings.Remove(Key.V.WithCtrl);
        KeyBindings.Remove(new Key(KeyCode.Insert).WithShift);
        KeyBindings.Add(Key.V.WithCtrl, [PasteCommand]);
        KeyBindings.Add(new Key(KeyCode.Insert).WithShift, [PasteCommand]);
    }
}
