namespace PiSharp.Git;

/// <summary>
/// Captures the authoritative change inventory: repo guard, branch/HEAD, porcelain
/// status, unmerged detection, and classification.
/// </summary>
public sealed class CommitInventoryService(IGitRunner git, ChangeClassifier classifier)
{
    public sealed record CaptureResult(
        bool Success,
        string? Error,
        string? RepoRoot,
        ChangeInventory? Inventory);

    public sealed record CaptureOptions(
        IReadOnlyList<string>? ScopeFiles = null,
        IReadOnlyList<string>? ExtraExcludes = null,
        bool IncludeStaged = true,
        bool IncludeUnstaged = true);

    public Task<CaptureResult> CaptureAsync(string cwd, CancellationToken cancellationToken = default)
        => CaptureAsync(cwd, new CaptureOptions(), cancellationToken);

    public async Task<CaptureResult> CaptureAsync(string cwd, CaptureOptions options, CancellationToken cancellationToken = default)
    {
        var guard = await git.RunAsync(cwd, ["rev-parse", "--is-inside-work-tree"], null, cancellationToken);
        if (guard.ExitCode != 0)
        {
            return new CaptureResult(false, "Not a git repository. Run `git init` first, or move into a git work tree.", null, null);
        }

        var toplevel = await git.RunAsync(cwd, ["rev-parse", "--show-toplevel"], null, cancellationToken);
        if (toplevel.ExitCode != 0)
        {
            return new CaptureResult(false, $"Could not resolve the repository root: {toplevel.Stderr.Trim()}", null, null);
        }

        var repoRoot = toplevel.Stdout.Trim();

        var branchResult = await git.RunAsync(repoRoot, ["branch", "--show-current"], null, cancellationToken);
        var branch = branchResult.ExitCode == 0 ? branchResult.Stdout.Trim() : null;

        var headResult = await git.RunAsync(repoRoot, ["rev-parse", "HEAD"], null, cancellationToken);
        var headHash = headResult.ExitCode == 0 ? headResult.Stdout.Trim() : null;

        var statusResult = await git.RunAsync(
            repoRoot,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            null,
            cancellationToken);
        if (statusResult.ExitCode != 0)
        {
            return new CaptureResult(false, $"git status failed: {statusResult.Stderr.Trim()}", repoRoot, null);
        }

        var rawItems = PorcelainParser.Parse(statusResult.Stdout);
        var unmerged = rawItems.FirstOrDefault(item => PorcelainParser.IsUnmerged(item.Status));
        if (unmerged is not null)
        {
            return new CaptureResult(false,
                $"Unresolved merge conflicts: '{unmerged.Path}' is in state '{unmerged.Status}'. Resolve conflicts first.",
                repoRoot, null);
        }

        var classified = classifier.Classify(rawItems, options.ScopeFiles, options.ExtraExcludes, options.IncludeStaged, options.IncludeUnstaged);
        var inventory = new ChangeInventory(headHash, branch, classified.Changes, classified.ExcludedFiles);
        return new CaptureResult(true, null, repoRoot, inventory);
    }
}
