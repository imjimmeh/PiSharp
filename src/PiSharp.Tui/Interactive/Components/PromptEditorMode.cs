namespace PiSharp.Tui.Interactive.Components;

internal interface IPromptEditorMode
{
    bool AllowsEmptySubmit { get; }
    string AcceptedSuggestionSuffix { get; }
    bool ShouldMoveSuggestions(string promptText, int suggestionCount);
    bool ShouldSubmitSelectedSuggestion(string submittedText, PromptCompletion? selectedSuggestion);
}

internal sealed class SlashCommandPromptMode(string acceptedSuggestionSuffix) : IPromptEditorMode
{
    public bool AllowsEmptySubmit => false;
    public string AcceptedSuggestionSuffix { get; } = acceptedSuggestionSuffix;

    public bool ShouldMoveSuggestions(string promptText, int suggestionCount)
        => suggestionCount > 0;

    public bool ShouldSubmitSelectedSuggestion(string submittedText, PromptCompletion? selectedSuggestion)
        => selectedSuggestion is not null
            && PromptEditorText.IsSlashCommandDraft(submittedText)
            && !submittedText.Contains(" ", StringComparison.Ordinal)
            && !string.Equals(submittedText, selectedSuggestion.Value, StringComparison.Ordinal);
}

internal sealed class InlineSelectionPromptMode(string acceptedSuggestionSuffix) : IPromptEditorMode
{
    public bool AllowsEmptySubmit => true;
    public string AcceptedSuggestionSuffix { get; } = acceptedSuggestionSuffix;

    public bool ShouldMoveSuggestions(string promptText, int suggestionCount)
        => suggestionCount > 0;

    public bool ShouldSubmitSelectedSuggestion(string submittedText, PromptCompletion? selectedSuggestion)
        => selectedSuggestion is not null;
}

internal static class PromptEditorText
{
    public static bool IsSlashCommandDraft(string text)
        => text.TrimStart().StartsWith("/", StringComparison.Ordinal);
}
