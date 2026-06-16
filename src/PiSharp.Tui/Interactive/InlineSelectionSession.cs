namespace PiSharp.Tui.Interactive;

public sealed class InlineSelectionSession(string title, IReadOnlyList<string> options)
{
    public string Title { get; } = title;
    public IReadOnlyList<string> Options { get; } = options;

    public IReadOnlyList<string> Complete(string input)
        => TuiFuzzyMatcher.Filter(Options, input, option => option).Take(10).ToArray();

    public string? Resolve(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return Complete(string.Empty).FirstOrDefault();
        return Complete(trimmed).FirstOrDefault() ?? trimmed;
    }
}
