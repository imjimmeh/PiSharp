namespace PiSharp.Tui.Interactive.Components;

public static class DiffView
{
    public static IReadOnlyList<string> Render(string diff)
        => (diff ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(line => line switch
        {
            _ when line.StartsWith("+", StringComparison.Ordinal) => "+ " + line[1..],
            _ when line.StartsWith("-", StringComparison.Ordinal) => "- " + line[1..],
            _ when line.StartsWith("@@", StringComparison.Ordinal) => "§ " + line,
            _ => "  " + line
        }).ToArray();
}
