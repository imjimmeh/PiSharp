namespace PiSharp.Tui.Interactive.Components;

internal sealed class PromptHistory(int maxEntries)
{
    private readonly List<string> _entries = [];
    private int _cursor = -1;
    private string _draftBeforeHistory = string.Empty;

    public IReadOnlyList<string> Entries => _entries;
    public bool IsNavigating => _cursor >= 0;

    public void Record(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        if (_entries.Count == 0 || !string.Equals(_entries[^1], trimmed, StringComparison.Ordinal))
        {
            _entries.Add(trimmed);
            if (_entries.Count > maxEntries) _entries.RemoveAt(0);
        }

        ResetNavigation();
    }

    public bool TryMove(string currentDraft, int delta, out string text)
    {
        text = currentDraft;
        if (_entries.Count == 0 || delta == 0) return false;
        if (_cursor < 0)
        {
            _draftBeforeHistory = currentDraft;
            _cursor = _entries.Count;
        }

        var next = Math.Clamp(_cursor + delta, 0, _entries.Count);
        if (next == _cursor) return true;

        _cursor = next;
        text = _cursor == _entries.Count ? _draftBeforeHistory : _entries[_cursor];
        return true;
    }

    public void ResetNavigation()
    {
        _cursor = -1;
        _draftBeforeHistory = string.Empty;
    }
}
