namespace PiSharp.DeclarativeTools;

public enum ToolLoadStatus
{
    Loaded,
    Skipped
}

/// <summary>
/// One per-file outcome of a discovery run (plan §5.4): loaded tools and
/// skipped files with the reason they were skipped.
/// </summary>
public sealed record ToolLoadEntry(string Name, ToolLoadStatus Status, string Message);

/// <summary>
/// In-memory result of the latest discovery pass, surfaced to users through the
/// <c>/declarative-tools</c> slash command.
/// </summary>
public sealed record DeclarativeToolLoadReport(
    DateTimeOffset Timestamp,
    IReadOnlyList<string> Directories,
    IReadOnlyList<ToolLoadEntry> Entries,
    bool Disabled)
{
    public static DeclarativeToolLoadReport None { get; } = new(DateTimeOffset.MinValue, [], [], Disabled: false);

    public IReadOnlyList<ToolLoadEntry> Loaded => Entries.Where(e => e.Status == ToolLoadStatus.Loaded).ToArray();

    public IReadOnlyList<ToolLoadEntry> Skipped => Entries.Where(e => e.Status == ToolLoadStatus.Skipped).ToArray();

    public string Format()
    {
        if (Disabled) return "Declarative tools are disabled (extensions.pisharp-declarative-tools.enabled = false).";
        if (Entries.Count == 0)
            return Directories.Count == 0
                ? "No tool directories configured."
                : $"Scanned: {string.Join(", ", Directories)}. No tool files found.";

        var lines = new List<string> { $"Scanned {Directories.Count} tool director{(Directories.Count == 1 ? "y" : "ies")}: {string.Join(", ", Directories)}" };
        var loaded = Loaded;
        var skipped = Skipped;
        lines.Add($"Loaded {loaded.Count} tool{(loaded.Count == 1 ? "" : "s")}: {string.Join(", ", loaded.Select(e => e.Name))}");
        if (skipped.Count > 0)
        {
            lines.Add($"Skipped {skipped.Count} file{(skipped.Count == 1 ? "" : "s")}:");
            lines.AddRange(skipped.Select(e => $"  - {e.Name}: {e.Message}"));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
