namespace PiSharp.Cli.Commands;

public static class ChangelogParser
{
    public sealed record ChangelogEntry(int Major, int Minor, int Patch, string Content);

    public static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        ChangelogEntry? current = null;
        var currentLines = new List<string>();

        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith("## "))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"##\s+\[?(\d+)\.(\d+)\.(\d+)\]?");
                if (match.Success)
                {
                    if (current is not null && currentLines.Count > 0)
                    {
                        entries.Add(current with { Content = string.Join("\n", currentLines).Trim() });
                    }
                    current = new ChangelogEntry(
                        int.Parse(match.Groups[1].Value),
                        int.Parse(match.Groups[2].Value),
                        int.Parse(match.Groups[3].Value),
                        "");
                    currentLines = [line];
                }
                else
                {
                    current = null;
                    currentLines = [];
                }
            }
            else if (current is not null)
            {
                currentLines.Add(line);
            }
        }

        if (current is not null && currentLines.Count > 0)
            entries.Add(current with { Content = string.Join("\n", currentLines).Trim() });

        return entries;
    }

    public static string? FindChangelogPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CHANGELOG.md"),
            Path.Combine(Environment.CurrentDirectory, "CHANGELOG.md"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
