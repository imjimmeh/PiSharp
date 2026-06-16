namespace PiSharp.Tui.Interactive.Components;

public sealed record PromptCompletion(
    string Value,
    string Label,
    string? Description = null,
    string Prefix = "",
    bool AppendSpace = false)
{
    public static PromptCompletion FromText(string value, string acceptedSuggestionSuffix = "")
        => new(value, value, Prefix: value, AppendSpace: !string.IsNullOrEmpty(acceptedSuggestionSuffix));
}
