namespace PiSharp.Tui.Interactive.Components;

internal sealed class PromptSuggestionState
{
    public IReadOnlyList<PromptCompletion> Suggestions { get; private set; } = [];
    public int SelectedIndex { get; private set; } = -1;
    public PromptCompletion? SelectedCompletion
        => SelectedIndex >= 0 && SelectedIndex < Suggestions.Count ? Suggestions[SelectedIndex] : null;
    public string? SelectedSuggestion => SelectedCompletion?.Value;

    public bool Refresh(string text, int cursorOffset, Func<string, int, IReadOnlyList<PromptCompletion>>? complete)
    {
        var next = complete is not null ? complete(text, cursorOffset) : [];
        return Replace(next);
    }

    public bool Replace(IReadOnlyList<PromptCompletion> next)
    {
        if (Suggestions.SequenceEqual(next)) return false;

        Suggestions = next;
        SelectedIndex = Suggestions.Count == 0 ? -1 : 0;
        return true;
    }

    public bool Clear()
    {
        if (Suggestions.Count == 0 && SelectedIndex == -1) return false;

        Suggestions = [];
        SelectedIndex = -1;
        return true;
    }

    public bool MoveSelection(int delta)
    {
        if (Suggestions.Count == 0 || delta == 0) return false;
        if (SelectedIndex < 0) SelectedIndex = 0;

        var next = SelectedIndex + delta;
        if (next < 0) next = Suggestions.Count - 1;
        if (next >= Suggestions.Count) next = 0;

        if (next == SelectedIndex) return false;
        SelectedIndex = next;
        return true;
    }
}
