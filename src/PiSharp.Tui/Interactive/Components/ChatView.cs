using PiSharp.Tui.Interactive.Rendering;
using PiSharp.Tui.Interactive.Theme;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public sealed class ChatView : WidthReflowView
{
    private readonly record struct InteractionPress(TuiInteractionTarget Target, int ViewRow, int Column);

    public const int ChatHorizontalGutter = 2;

    private readonly ChatRowCache _cache = new(TuiMessageRenderer.Render);
    private readonly ChatTranscriptRowPlanner _rowPlanner = new();
    private readonly TextSelectionManager _selection;
    private IReadOnlyList<TuiChatRow> _rows = [];
    private IReadOnlyList<TuiTranscriptItem>? _lastRenderedTranscript;
    private IReadOnlyList<TuiBridgeSlot>? _lastRenderedBridgeSlots;
    private int _lastRenderedWidth;
    private int _lastRenderedHeight;
    private bool _lastRenderedShowThinking;
    private bool _lastRenderedShowToolOutput;
    private bool _hasRenderedRows;
    private int _scrollTop;
    private bool _autoScroll = true;
    private InteractionPress? _interactionPress;
    private bool _interactionPressMoved;

    public event Action<TuiInteractionHit>? InteractionRequested;
    public event Action<TuiInteractionHit>? ContextMenuRequested;
    public event Action<string, bool>? SelectionCopied;

    public IReadOnlyList<TuiChatRow> Rows => _rows;
    public int ScrollTop => _scrollTop;
    public int ScrollableRowCount => _rows.Count;
    public int VisibleRowCount => GetVisibleRowCount();
    public bool IsAutoScroll => _autoScroll;
    public bool HasSelection => _selection.HasSelection;
    public string SelectedText => _selection.SelectedText;
    public Func<string, bool> ClipboardWriter { get; set; } = text => Clipboard.TrySetClipboardData(text);
    public Func<TuiTranscriptItem, TuiRenderState, int, IReadOnlyList<TuiChatRow>> TranscriptItemRenderer
    {
        get => _cache.Renderer;
        set => _cache.Renderer = value;
    }

    internal TuiProfilingCounters? ProfilingCounters { get; set; }

    public ChatView() : base(fallbackWidth: 100)
    {
        _selection = new TextSelectionManager(
            selectableText: sourceRow => sourceRow >= 0 && sourceRow < _rows.Count ? _rows[sourceRow].Text.Trim() : string.Empty,
            visibleRowCount: () => GetVisibleRowCount(),
            scrollTop: () => _scrollTop,
            sourceRowCount: () => _rows.Count);
        _selection.SelectionChanged += SetNeedsDraw;
        _selection.DragBegan += () => _autoScroll = false;

        CanFocus = false;
        WantMousePositionReports = true;
        WantContinuousButtonPressed = true;
    }

    public void Render(TuiRenderState state)
    {
        var width = TrackRenderWidth(() => Render(state));
        if (CanReuseRows(state, width))
        {
            ReuseRowsForCurrentLayout();
            return;
        }

        var preserveScrollTop = _selection.IsDragInProgress ? _scrollTop : (int?)null;
        var rows = _rowPlanner.BuildRows(state, width, _cache, ProfilingCounters);

        _rows = rows.Count == 0 ? [ChatRowCache.ApplyHorizontalPadding(new TuiChatRow("Ready. Type /help for commands."), width)] : rows;
        _lastRenderedTranscript = state.Transcript;
        _lastRenderedBridgeSlots = state.BridgeSlots;
        _lastRenderedWidth = width;
        _lastRenderedHeight = Frame.Height;
        _lastRenderedShowThinking = state.ShowThinking;
        _lastRenderedShowToolOutput = state.ShowToolOutput;
        _hasRenderedRows = true;
        if (preserveScrollTop is not null)
        {
            _scrollTop = Math.Clamp(preserveScrollTop.Value, 0, MaxScrollTop());
            _autoScroll = false;
        }
        else if (_autoScroll) ScrollToEnd();
        else ClampScrollTop();
        SetNeedsDraw();
    }

    private bool CanReuseRows(TuiRenderState state, int width)
        => _hasRenderedRows
            && ReferenceEquals(_lastRenderedTranscript, state.Transcript)
            && ReferenceEquals(_lastRenderedBridgeSlots, state.BridgeSlots)
            && _lastRenderedWidth == width
            && _lastRenderedShowThinking == state.ShowThinking
            && _lastRenderedShowToolOutput == state.ShowToolOutput;

    private void ReuseRowsForCurrentLayout()
    {
        var previousScrollTop = _scrollTop;
        var previousAutoScroll = _autoScroll;
        var previousHeight = _lastRenderedHeight;

        if (_autoScroll)
        {
            _scrollTop = MaxScrollTop();
            _autoScroll = true;
        }
        else
        {
            ClampScrollTop();
        }

        _lastRenderedHeight = Frame.Height;
        if (_scrollTop != previousScrollTop || _autoScroll != previousAutoScroll || _lastRenderedHeight != previousHeight)
        {
            SetNeedsDraw();
        }
    }

    public void ScrollLine(int delta) => ScrollBy(delta);
    public void ScrollPage(int delta) => ScrollBy(delta * GetVisibleRowCount());

    public void ScrollTo(int position)
    {
        _autoScroll = false;
        _scrollTop = Math.Clamp(position, 0, MaxScrollTop());
        if (_scrollTop == MaxScrollTop()) _autoScroll = true;
        SetNeedsDraw();
    }

    public void ScrollToStart()
    {
        _scrollTop = 0;
        _autoScroll = false;
        SetNeedsDraw();
    }

    public void ScrollToEnd()
    {
        _scrollTop = MaxScrollTop();
        _autoScroll = true;
        SetNeedsDraw();
    }

    public bool TryGetInteractionHit(int viewRow, int column, out TuiInteractionHit? hit)
        => TryGetHit(viewRow, column, row => row.InteractionTarget, out hit);

    public bool TryGetContextHit(int viewRow, int column, out TuiInteractionHit? hit)
        => TryGetHit(viewRow, column, row => row.ContextTarget, out hit);

    private bool TryGetHit(int viewRow, int column, Func<TuiChatRow, TuiInteractionTarget?> targetSelector, out TuiInteractionHit? hit)
    {
        hit = null;
        if (viewRow < 0) return false;

        var sourceRow = _scrollTop + viewRow;
        if (sourceRow < 0 || sourceRow >= _rows.Count) return false;

        var target = targetSelector(_rows[sourceRow]);
        if (target is null) return false;

        hit = new TuiInteractionHit(target, sourceRow, viewRow, column);
        return true;
    }

    public bool CopySelectionToClipboard()
    {
        var selectedText = _selection.SelectedText;
        if (string.IsNullOrEmpty(selectedText)) return false;

        var copied = TryCopySelection(selectedText);
        SelectionCopied?.Invoke(selectedText, copied);
        _selection.ClearSelection();
        return copied;
    }

    internal static IReadOnlyList<TuiChatRow> RenderBridgeSlot(TuiBridgeSlot slot, int width)
    {
        var contentWidth = ChatRowCache.ContentWidth(width);
        var prefix = $"[{slot.Title}] ";
        var lines = SplitLines(slot.Content).ToArray();
        if (lines.Length <= 1)
        {
            return TuiAnsiText.Rows(prefix + (lines.FirstOrDefault() ?? string.Empty), TuiChatRowKind.Normal, contentWidth, slot.InteractionTarget)
                .Select(row => ChatRowCache.ApplyHorizontalPadding(row, width))
                .ToArray();
        }

        var rows = new List<TuiChatRow>();
        rows.AddRange(TuiAnsiText.Rows(prefix.TrimEnd(), TuiChatRowKind.Normal, contentWidth, slot.InteractionTarget));
        foreach (var line in lines)
        {
            rows.AddRange(TuiAnsiText.Rows(line, TuiChatRowKind.Normal, contentWidth, slot.InteractionTarget));
        }

        return rows.Select(row => ChatRowCache.ApplyHorizontalPadding(row, width)).ToArray();
    }

    private static IEnumerable<string> SplitLines(string content)
        => (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    protected override bool OnMouseEvent(MouseEventArgs args)
    {
        if (TryHandleMouseWheel(args)) return true;

        // WantMousePositionReports can deliver button events for positions outside our bounds.
        // Guard against accidentally grabbing the mouse or consuming clicks that belong to
        // other views (e.g. the scrollbar or menu bar), but only when no drag or interaction
        // is already in progress (those need global tracking to handle release outside bounds).
        if (!_selection.IsDragInProgress && _interactionPress is null)
        {
            var inBounds = args.Position.Y >= 0 && args.Position.Y < Frame.Height;
            if (!inBounds &&
                (args.Flags.HasFlag(MouseFlags.Button1Pressed) ||
                 args.Flags.HasFlag(MouseFlags.Button3Pressed) ||
                 args.Flags.HasFlag(MouseFlags.Button3Clicked)))
            {
                return base.OnMouseEvent(args);
            }
        }

        if (args.Flags.HasFlag(MouseFlags.Button3Clicked) || args.Flags.HasFlag(MouseFlags.Button3Pressed))
        {
            if (TryRequestContextMenu(args)) return true;
        }

        if (args.Flags.HasFlag(MouseFlags.Button1Pressed))
        {
            if (_interactionPress is not null)
            {
                UpdateInteractionPress(args);
                args.Handled = true;
                return true;
            }

            if (_selection.IsDragInProgress) _selection.UpdateSelection(args.Position.Y, args.Position.X);
            else if (TryBeginInteractionPress(args)) return true;
            else BeginSelection(args.Position.Y, args.Position.X);

            args.Handled = true;
            return true;
        }

        if (_interactionPress is not null && args.Flags.HasFlag(MouseFlags.ReportMousePosition))
        {
            UpdateInteractionPress(args);
            args.Handled = true;
            return true;
        }

        if (_interactionPress is not null && args.Flags.HasFlag(MouseFlags.Button1Released))
        {
            CompleteInteractionPress(args);
            return true;
        }

        if (_selection.IsDragInProgress && args.Flags.HasFlag(MouseFlags.ReportMousePosition))
        {
            _selection.UpdateSelection(args.Position.Y, args.Position.X);
            args.Handled = true;
            return true;
        }

        if (_selection.IsDragInProgress && args.Flags.HasFlag(MouseFlags.Button1Released))
        {
            _selection.UpdateSelection(args.Position.Y, args.Position.X);
            _selection.EndDrag();
            TryUngrabMouse();

            if (_selection.IsSelectionMoved)
            {
                _selection.SuppressStandaloneClick(args.Position.Y, args.Position.X);
                if (_selection.HasSelection)
                {
                    args.Handled = true;
                    return true;
                }
            }

            _selection.ClearSelection();
            TryUngrabMouse();
            args.Handled = true;
            return true;
        }

        return base.OnMouseEvent(args);
    }

    protected override bool OnMouseClick(MouseEventArgs args)
    {
        if (args.Flags.HasFlag(MouseFlags.Button3Clicked))
        {
            return TryRequestContextMenu(args) || base.OnMouseClick(args);
        }

        if (!args.Flags.HasFlag(MouseFlags.Button1Clicked)) return base.OnMouseClick(args);
        if (TrySuppressStandaloneInteractionClick(args)) return true;
        return TryRequestInteraction(args) || base.OnMouseClick(args);
    }

    protected override bool OnMouseWheel(MouseEventArgs args)
        => TryHandleMouseWheel(args) || base.OnMouseWheel(args);

    private bool TryHandleMouseWheel(MouseEventArgs args)
    {
        if (!args.IsWheel) return false;

        if (args.Flags.HasFlag(MouseFlags.WheeledUp)) ScrollLine(-1);
        else if (args.Flags.HasFlag(MouseFlags.WheeledDown)) ScrollLine(1);
        else return false;

        args.Handled = true;
        return true;
    }

    private bool TryBeginInteractionPress(MouseEventArgs args)
    {
        if (!TryGetInteractionHit(args.Position.Y, args.Position.X, out var hit) || hit is null) return false;

        _selection.ClearSelection();
        TryUngrabMouse();
        _interactionPress = new InteractionPress(hit.Target, args.Position.Y, args.Position.X);
        _interactionPressMoved = false;
        args.Handled = true;
        return true;
    }

    private bool TryRequestContextMenu(MouseEventArgs args)
    {
        if (!TryGetContextHit(args.Position.Y, args.Position.X, out var hit) || hit is null) return false;

        _selection.ClearSelection();
        TryUngrabMouse();
        args.Handled = true;
        ContextMenuRequested?.Invoke(hit);
        return true;
    }

    private bool TryRequestInteraction(MouseEventArgs args)
    {
        if (!TryGetInteractionHit(args.Position.Y, args.Position.X, out var hit) || hit is null) return false;

        _selection.ClearSelection();
        TryUngrabMouse();
        args.Handled = true;
        InteractionRequested?.Invoke(hit);
        return true;
    }

    private bool TrySuppressStandaloneInteractionClick(MouseEventArgs args)
    {
        if (!_selection.TryConsumeSuppressedClick(args.Position.Y, args.Position.X)) return false;

        args.Handled = true;
        return true;
    }

    private void UpdateInteractionPress(MouseEventArgs args)
    {
        if (_interactionPress is not { } press) return;

        _interactionPressMoved = _interactionPressMoved || press.ViewRow != args.Position.Y || press.Column != args.Position.X;
    }

    private void CompleteInteractionPress(MouseEventArgs args)
    {
        var press = _interactionPress;
        _interactionPress = null;
        _selection.SuppressStandaloneClick(args.Position.Y, args.Position.X);
        args.Handled = true;

        if (press is null || _interactionPressMoved) return;
        if (!TryGetInteractionHit(args.Position.Y, args.Position.X, out var hit) || hit is null) return;
        if (!Equals(hit.Target, press.Value.Target)) return;

        InteractionRequested?.Invoke(hit);
    }

    private void ScrollBy(int delta)
    {
        if (delta == 0) return;
        _autoScroll = false;
        _scrollTop = Math.Clamp(_scrollTop + delta, 0, MaxScrollTop());
        if (_scrollTop == MaxScrollTop()) _autoScroll = true;
        SetNeedsDraw();
    }

    private void ClampScrollTop() => _scrollTop = Math.Clamp(_scrollTop, 0, MaxScrollTop());
    private int GetVisibleRowCount() => Math.Max(1, Frame.Height > 0 ? Frame.Height : 10);
    private int MaxScrollTop() => Math.Max(0, _rows.Count - GetVisibleRowCount());

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Math.Max(0, Frame.Width);
        var height = Math.Max(0, Frame.Height);
        for (var viewRow = 0; viewRow < height; viewRow++)
        {
            var sourceRow = _scrollTop + viewRow;
            var row = sourceRow < _rows.Count ? _rows[sourceRow].PadTo(width) : new TuiChatRow(string.Empty).PadTo(width);
            Move(0, viewRow);
            DrawRow(sourceRow, row);
        }
        return true;
    }

    private void BeginSelection(int viewRow, int column)
    {
        if (_selection.TryBeginSelection(viewRow, column))
        {
            TryGrabMouse();
        }
    }

    private void DrawRow(int sourceRow, TuiChatRow row)
    {
        var rowAttribute = TuiTheme.ChatRowAttribute(row.Kind);
        if (!_selection.TryGetSelectionRangeForRow(sourceRow, out var selectionStart, out var selectionEnd))
        {
            DrawStyledRow(row, rowAttribute);
            return;
        }

        selectionStart = Math.Clamp(selectionStart, 0, row.Text.Length);
        selectionEnd = Math.Clamp(selectionEnd, selectionStart, row.Text.Length);

        SetAttribute(rowAttribute);
        AddStr(row.Text[..selectionStart]);
        SetAttribute(TuiTheme.SelectionAttribute);
        AddStr(row.Text[selectionStart..selectionEnd]);
        SetAttribute(rowAttribute);
        AddStr(row.Text[selectionEnd..]);
    }

    private void DrawStyledRow(TuiChatRow row, Terminal.Gui.Attribute rowAttribute)
    {
        if (row.Spans is null || row.Spans.Count == 0)
        {
            SetAttribute(rowAttribute);
            AddStr(row.Text);
            return;
        }

        var drawn = 0;
        foreach (var span in row.Spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;
            SetAttribute(TuiTheme.SpanAttribute(span.Kind, rowAttribute));
            AddStr(span.Text);
            drawn += span.Text.Length;
        }

        if (drawn < row.Text.Length)
        {
            SetAttribute(rowAttribute);
            AddStr(row.Text[drawn..]);
        }
    }

    private bool TryCopySelection(string selectedText)
    {
        try
        {
            return ClipboardWriter(selectedText);
        }
        catch
        {
            return false;
        }
    }

    private void TryGrabMouse()
    {
        try
        {
            Application.GrabMouse(this);
        }
        catch
        {
            // Unit tests can exercise ChatView without an initialized Application.
        }
    }

    private void TryUngrabMouse()
    {
        try
        {
            if (Application.MouseGrabView == this) Application.UngrabMouse();
        }
        catch
        {
            // Unit tests can exercise ChatView without an initialized Application.
        }
    }
}
