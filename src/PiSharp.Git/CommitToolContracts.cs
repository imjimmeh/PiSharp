namespace PiSharp.Git;

/// <summary>
/// Classification of a changed file for scoring and auto-grouping.
/// Ordering used by the scoring table: Source &gt; Test &gt; Docs &gt; Config &gt; Other.
/// </summary>
public enum ChangeCategory
{
    Source,
    Test,
    Docs,
    Config,
    Other
}

/// <summary>
/// One changed file from <c>git status --porcelain=v1 -z</c>, classified.
/// </summary>
public sealed record ChangeItem(
    string Path,
    string Status,
    ChangeCategory Category,
    int Score,
    bool IsRename,
    string? RenameSource)
{
    /// <summary>Porcelain XY code (e.g. "M ", "MM", "??", "RM").</summary>
    public char IndexStatus => Status.Length > 0 ? Status[0] : ' ';

    /// <summary>True when the change is present in the index (porcelain X column).</summary>
    public bool IsStaged => !IsUntracked && Status.Length > 0 && Status[0] != ' ';

    /// <summary>True when the change is present in the worktree (porcelain Y column or untracked).</summary>
    public bool IsUnstaged => Status.Length > 1 && Status[1] != ' ' || IsUntracked;

    public bool IsUntracked => Status is "??";
}

/// <summary>
/// The authoritative change inventory returned by the <c>commit</c> tool's inventory call.
/// </summary>
public sealed record ChangeInventory(
    string? HeadHash,
    string? Branch,
    IReadOnlyList<ChangeItem> Changes,
    IReadOnlyList<string> ExcludedFiles);

/// <summary>
/// Input for the model-facing <c>commit</c> tool.
/// Two-call contract: call 1 with <see cref="Groups"/> null returns the inventory (no commits);
/// call 2 supplies <see cref="Groups"/> and executes. Single-commit mode: <see cref="Message"/>
/// plus <c>Split = false</c>.
/// </summary>
public sealed record CommitToolInput
{
    public IReadOnlyList<CommitGroupInput>? Groups { get; init; }

    public string? Message { get; init; }

    public bool Split { get; init; } = true;

    public bool DryRun { get; init; }

    public bool IncludeStaged { get; init; } = true;

    public bool IncludeUnstaged { get; init; } = true;

    public IReadOnlyList<string>? Files { get; init; }

    public IReadOnlyList<string>? Exclude { get; init; }

    public bool AutoConfirm { get; init; } = true;
}

/// <summary>
/// One proposed commit group. <see cref="Files"/> are exact inventory paths (globs rejected);
/// <see cref="DependsOn"/> references other groups' ids (committed first).
/// </summary>
public sealed record CommitGroupInput
{
    public required string Message { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public IReadOnlyList<string>? DependsOn { get; init; }

    public string? Id { get; init; }
}

/// <summary>One executed commit.</summary>
public sealed record CommitExecuted(string Hash, string Message, IReadOnlyList<string> Files);

/// <summary>
/// Result of an execute call: the commits created (topological order), files excluded by policy,
/// and changes left uncommitted (abort path / dry-run remainder).
/// </summary>
public sealed record CommitToolDetails(
    bool WasDryRun,
    IReadOnlyList<CommitExecuted> Commits,
    IReadOnlyList<string> ExcludedFiles,
    IReadOnlyList<string> RemainingFiles,
    IReadOnlyList<string>? RejectedCycle = null);
