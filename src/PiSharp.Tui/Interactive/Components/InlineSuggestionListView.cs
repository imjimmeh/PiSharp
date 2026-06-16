namespace PiSharp.Tui.Interactive.Components;

public sealed class InlineSuggestionListView : WrappedTextView
{
    private int? _maxVisibleLines;
    private int? _maxVisibleItems;
    private bool _singleLineItems;

    public IReadOnlyList<PromptCompletion> Completions { get; private set; } = [];
    public IReadOnlyList<string> Suggestions => Completions.Select(item => item.Value).ToArray();
    public int SelectedIndex { get; private set; }
    public int FirstVisibleIndex { get; private set; }
    public int VisibleRowCount => Suggestions.Count;

    public InlineSuggestionListView() : base(fallbackWidth: 100)
    {
        CanFocus = false;
    }

    public void SetSuggestions(IEnumerable<string> suggestions, int selectedIndex = -1)
        => SetCompletions(suggestions.Select(value => new PromptCompletion(value, value)), selectedIndex);

    public void SetCompletions(
        IEnumerable<PromptCompletion> suggestions,
        int selectedIndex = -1,
        int? maxVisibleLines = null,
        int? maxVisibleItems = null,
        bool singleLineItems = false)
    {
        var next = suggestions.Where(suggestion => !string.IsNullOrWhiteSpace(suggestion.Value)).DistinctBy(suggestion => suggestion.Value, StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedSelected = -1;
        if (next.Length > 0)
        {
            var candidate = selectedIndex < 0 ? 0 : selectedIndex;
            normalizedSelected = Math.Clamp(candidate, 0, next.Length - 1);
        }
        if (Completions.SequenceEqual(next)
            && SelectedIndex == normalizedSelected
            && _maxVisibleLines == maxVisibleLines
            && _maxVisibleItems == maxVisibleItems
            && _singleLineItems == singleLineItems) return;
        Completions = next;
        SelectedIndex = normalizedSelected;
        _maxVisibleLines = maxVisibleLines;
        _maxVisibleItems = maxVisibleItems;
        _singleLineItems = singleLineItems;
        Render();
    }

    public string? Accept() => SelectedIndex >= 0 && SelectedIndex < Completions.Count ? Completions[SelectedIndex].Value : null;

    private void Render()
    {
        var width = TuiViewSizing.ResolveWidth(this, 100);
        var maxVisibleLines = ResolveMaxVisibleLines();
        var maxVisibleItems = ResolveMaxVisibleItems();
        EnsureSelectedVisible(width, maxVisibleLines, maxVisibleItems);

        RenderWrapped(VisibleCompletions(width, maxVisibleLines, maxVisibleItems), Render);
    }

    private IEnumerable<string> VisibleCompletions(int width, int maxVisibleLines, int maxVisibleItems)
    {
        var usedLines = 0;
        var usedItems = 0;
        for (var index = FirstVisibleIndex; index < Completions.Count; index++)
        {
            if (usedItems >= maxVisibleItems) yield break;

            var line = FormatCompletion(Completions[index], index);
            if (_singleLineItems) line = FitSingleLine(line, width);
            var lineCount = _singleLineItems ? 1 : WrappedLineCount(line, width);
            if (usedLines > 0 && usedLines + lineCount > maxVisibleLines) yield break;

            yield return line;
            usedLines += lineCount;
            usedItems++;
        }
    }

    private void EnsureSelectedVisible(int width, int maxVisibleLines, int maxVisibleItems)
    {
        if (Completions.Count == 0 || SelectedIndex < 0)
        {
            FirstVisibleIndex = 0;
            return;
        }

        FirstVisibleIndex = Math.Clamp(FirstVisibleIndex, 0, SelectedIndex);
        while (FirstVisibleIndex < SelectedIndex && SelectedIndex - FirstVisibleIndex >= maxVisibleItems)
        {
            FirstVisibleIndex++;
        }

        while (FirstVisibleIndex < SelectedIndex && VisibleLineCount(FirstVisibleIndex, SelectedIndex, width) > maxVisibleLines)
        {
            FirstVisibleIndex++;
        }
    }

    private int ResolveMaxVisibleLines()
    {
        var configured = _maxVisibleLines is > 0 ? _maxVisibleLines.Value : int.MaxValue;
        if (_maxVisibleLines is null) return configured;

        if (Frame.Height > 0) configured = Math.Min(configured, Frame.Height);
        if (Viewport.Height > 0) configured = Math.Min(configured, Viewport.Height);

        return Math.Max(1, configured);
    }

    private int ResolveMaxVisibleItems()
        => _maxVisibleItems is > 0 ? _maxVisibleItems.Value : int.MaxValue;

    private int VisibleLineCount(int startIndex, int endIndex, int width)
        => _singleLineItems ? endIndex - startIndex + 1 : WrappedLineCount(startIndex, endIndex, width);

    private int WrappedLineCount(int startIndex, int endIndex, int width)
    {
        var count = 0;
        for (var index = startIndex; index <= endIndex; index++)
        {
            count += WrappedLineCount(FormatCompletion(Completions[index], index), width);
        }
        return count;
    }

    private static int WrappedLineCount(string line, int width)
        => Math.Max(1, TuiViewSizing.WrapLines([line], width).Count);

    private string FormatCompletion(PromptCompletion suggestion, int index)
    {
        var text = suggestion.Description is null ? suggestion.Label : $"{suggestion.Label}  {suggestion.Description}";
        return index == SelectedIndex ? $"→ {text}" : $"  {text}";
    }

    private static string FitSingleLine(string line, int width)
    {
        if (line.Length <= width) return line;
        if (width <= 3) return line[..width];
        return line[..(width - 3)] + "...";
    }
}
