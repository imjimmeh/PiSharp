using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

internal sealed class TextSelectionManager
{
    internal readonly record struct TextSelectionPoint(int SourceRow, int Column);
    internal readonly record struct SuppressedClick(int ViewRow, int Column);

    private readonly Func<int, string> _selectableText;
    private readonly Func<int> _visibleRowCount;
    private readonly Func<int> _scrollTop;
    private readonly Func<int> _sourceRowCount;

    private TextSelectionPoint? _selectionAnchor;
    private TextSelectionPoint? _selectionActive;
    private bool _selectionDragInProgress;
    private bool _selectionMoved;
    private SuppressedClick? _suppressedStandaloneClick;

    public event Action? SelectionChanged;
    public event Action? DragBegan;

    public bool HasSelection => !string.IsNullOrEmpty(SelectedText);
    public string SelectedText => BuildSelectedText() ?? string.Empty;
    public bool IsDragInProgress => _selectionDragInProgress;
    public bool IsSelectionMoved => _selectionMoved;

    public TextSelectionManager(
        Func<int, string> selectableText,
        Func<int> visibleRowCount,
        Func<int> scrollTop,
        Func<int> sourceRowCount)
    {
        _selectableText = selectableText;
        _visibleRowCount = visibleRowCount;
        _scrollTop = scrollTop;
        _sourceRowCount = sourceRowCount;
    }

    public bool TryBeginSelection(int viewRow, int column)
    {
        if (!TryCreateSelectionPoint(viewRow, column, out var point))
        {
            ClearSelection();
            return false;
        }

        _selectionAnchor = point;
        _selectionActive = point;
        _selectionDragInProgress = true;
        _selectionMoved = false;
        DragBegan?.Invoke();
        SelectionChanged?.Invoke();
        return true;
    }

    public void UpdateSelection(int viewRow, int column)
    {
        if (!_selectionDragInProgress || !TryCreateSelectionPoint(viewRow, column, out var point)) return;

        _selectionMoved = _selectionMoved || point != _selectionAnchor;
        _selectionActive = point;
        SelectionChanged?.Invoke();
    }

    public void EndDrag()
    {
        _selectionDragInProgress = false;
    }

    public void ClearSelection()
    {
        _selectionAnchor = null;
        _selectionActive = null;
        _selectionDragInProgress = false;
        _selectionMoved = false;
        SelectionChanged?.Invoke();
    }

    public bool TryCreateSelectionPoint(int viewRow, int column, out TextSelectionPoint point)
    {
        point = default;
        var sc = _sourceRowCount();
        if (sc == 0) return false;

        viewRow = Math.Clamp(viewRow, 0, _visibleRowCount() - 1);
        var sourceRow = _scrollTop() + viewRow;
        if (sourceRow < 0 || sourceRow >= sc) return false;

        point = new TextSelectionPoint(sourceRow, TuiCellWidth.CellColumnToTextIndex(_selectableText(sourceRow), column));
        return true;
    }

    public string? BuildSelectedText()
    {
        if (!TryGetSelectionBounds(out var start, out var end)) return null;

        var lines = new List<string>();
        for (var rowIndex = start.SourceRow; rowIndex <= end.SourceRow; rowIndex++)
        {
            var line = _selectableText(rowIndex);
            var startColumn = rowIndex == start.SourceRow ? Math.Clamp(start.Column, 0, line.Length) : 0;
            var endColumn = rowIndex == end.SourceRow ? Math.Clamp(end.Column, 0, line.Length) : line.Length;
            lines.Add(endColumn <= startColumn ? string.Empty : line[startColumn..endColumn]);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public bool TryGetSelectionBounds(out TextSelectionPoint start, out TextSelectionPoint end)
    {
        start = default;
        end = default;
        if (_selectionAnchor is not { } anchor || _selectionActive is not { } active) return false;

        if (CompareSelectionPoints(anchor, active) <= 0)
        {
            start = anchor;
            end = IncludeActiveColumn(active);
        }
        else
        {
            start = active;
            end = IncludeActiveColumn(anchor);
        }

        return CompareSelectionPoints(start, end) < 0;
    }

    public bool TryGetSelectionRangeForRow(int sourceRow, out int startColumn, out int endColumn)
    {
        startColumn = 0;
        endColumn = 0;
        if (!TryGetSelectionBounds(out var start, out var end)) return false;
        if (sourceRow < start.SourceRow || sourceRow > end.SourceRow) return false;

        var lineLength = _selectableText(sourceRow).Length;
        startColumn = sourceRow == start.SourceRow ? Math.Clamp(start.Column, 0, lineLength) : 0;
        endColumn = sourceRow == end.SourceRow ? Math.Clamp(end.Column, 0, lineLength) : lineLength;
        return endColumn > startColumn;
    }

    public void SuppressStandaloneClick(int viewRow, int column)
        => _suppressedStandaloneClick = new SuppressedClick(viewRow, column);

    public bool TryConsumeSuppressedClick(int viewRow, int column)
    {
        if (_suppressedStandaloneClick is not { } suppressed) return false;

        _suppressedStandaloneClick = null;
        if (suppressed.ViewRow != viewRow || suppressed.Column != column) return false;

        return true;
    }

    private TextSelectionPoint IncludeActiveColumn(TextSelectionPoint point)
        => point with { Column = Math.Clamp(point.Column + 1, 0, _selectableText(point.SourceRow).Length) };

    private static int CompareSelectionPoints(TextSelectionPoint left, TextSelectionPoint right)
        => left.SourceRow != right.SourceRow ? left.SourceRow.CompareTo(right.SourceRow) : left.Column.CompareTo(right.Column);
}
