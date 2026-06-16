using System.Text.RegularExpressions;

namespace PiSharp.Tui.Interactive.Rendering;

public sealed record TuiParsedSkillInvocation(
    string Name,
    string Location,
    string Content,
    string? UserMessage);

public static partial class TuiSkillInvocationParser
{
    public static bool TryParse(string text, out TuiParsedSkillInvocation? invocation)
    {
        invocation = null;
        if (string.IsNullOrEmpty(text)) return false;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var match = SkillBlockRegex().Match(normalized);
        if (!match.Success) return false;

        var userMessage = match.Groups[4].Success ? match.Groups[4].Value.Trim() : null;
        invocation = new TuiParsedSkillInvocation(
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            string.IsNullOrWhiteSpace(userMessage) ? null : userMessage);
        return true;
    }

    [GeneratedRegex("^<skill name=\"([^\"]+)\" location=\"([^\"]+)\">\n([\\s\\S]*?)\n</skill>(?:\n\n([\\s\\S]+))?$")]
    private static partial Regex SkillBlockRegex();
}
