namespace PiSharp.Git;

/// <summary>
/// Executes validated commit groups against a real repository.
///
/// Per group: <c>git add -A -- &lt;explicit paths&gt;</c> (never a path-less add),
/// then a staged-vs-intended verification (<c>git diff --cached --name-only -z</c>
/// must equal the group's destination paths — rename detection shows only the new
/// path, verified empirically), then <c>git commit -F -</c> (message via stdin,
/// multi-line safe). A failure at group N stops the pipeline: groups 1..N-1 stay
/// committed and the rest are reported as remaining; no automatic rollback.
/// </summary>
public sealed class CommitExecutor(IGitRunner git, CommitGraph graph)
{
    public sealed record ExecuteRequest(
        string RepoRoot,
        ChangeInventory Inventory,
        IReadOnlyList<CommitGroupInput> Groups,
        bool RunHooks = true,
        bool DryRun = false);

    public sealed record ExecuteOutcome(
        bool Success,
        string? Error,
        CommitToolDetails? Details);

    /// <summary>Raised once per successfully created commit (after rev-parse HEAD).</summary>
    public event Action<CommitCreatedEvent>? CommitCreated;

    public async Task<ExecuteOutcome> ExecuteAsync(ExecuteRequest request, CancellationToken cancellationToken = default)
    {
        var build = graph.BuildAndOrder(request.Groups, request.Inventory.Changes);
        if (!build.IsValid)
        {
            return new ExecuteOutcome(false, build.Error, build.Cycle is null
                ? null
                : new CommitToolDetails(false, [], request.Inventory.ExcludedFiles, RemainingPaths(request), build.Cycle));
        }

        var ordered = build.Ordered!;
        var commits = new List<CommitExecuted>();
        var remaining = new List<string>();

        if (request.DryRun)
        {
            remaining.AddRange(RemainingPaths(request));
            return new ExecuteOutcome(true, null,
                new CommitToolDetails(true, commits, request.Inventory.ExcludedFiles, remaining));
        }

        var inventoryByPath = request.Inventory.Changes.ToDictionary(item => item.Path, StringComparer.Ordinal);
        var uncommitted = new HashSet<string>(inventoryByPath.Keys, StringComparer.Ordinal);

        foreach (var group in ordered)
        {
            var stagePaths = ExpandStagePaths(group.Files, inventoryByPath);

            var addResult = await git.RunAsync(request.RepoRoot, ["add", "-A", "--", .. stagePaths], null, cancellationToken);
            if (addResult.ExitCode != 0)
            {
                return new ExecuteOutcome(false,
                    $"git add failed for group '{group.Id}': {addResult.Stderr.Trim()}",
                    new CommitToolDetails(false, commits, request.Inventory.ExcludedFiles, [.. uncommitted]));
            }

            var verifyResult = await git.RunAsync(request.RepoRoot, ["diff", "--cached", "--name-only", "-z"], null, cancellationToken);
            if (verifyResult.ExitCode != 0)
            {
                return new ExecuteOutcome(false,
                    $"git diff --cached failed for group '{group.Id}': {verifyResult.Stderr.Trim()}",
                    new CommitToolDetails(false, commits, request.Inventory.ExcludedFiles, [.. uncommitted]));
            }

            var staged = ParseNameOnly(verifyResult.Stdout);
            var intended = new HashSet<string>(group.Files, StringComparer.Ordinal);
            if (!staged.SetEquals(intended))
            {
                var extra = staged.Except(intended, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
                var missing = intended.Except(staged, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
                return new ExecuteOutcome(false,
                    $"Staged set mismatch for group '{group.Id}'. " +
                    $"Staged but not intended: [{string.Join(", ", extra)}]. Intended but not staged: [{string.Join(", ", missing)}]. " +
                    "Aborting before commit — the working tree may have changed since the inventory was captured.",
                    new CommitToolDetails(false, commits, request.Inventory.ExcludedFiles, [.. uncommitted]));
            }

            var commitArgs = new List<string> { "commit", "-F", "-" };
            if (!request.RunHooks)
            {
                commitArgs.Add("--no-verify");
            }

            var commitResult = await git.RunAsync(request.RepoRoot, commitArgs, group.Message + "\n", cancellationToken);
            if (commitResult.ExitCode != 0)
            {
                return new ExecuteOutcome(false,
                    $"git commit failed for group '{group.Id}': {commitResult.Stderr.Trim()}",
                    new CommitToolDetails(false, commits, request.Inventory.ExcludedFiles, [.. uncommitted]));
            }
            var headResult = await git.RunAsync(request.RepoRoot, ["rev-parse", "HEAD"], null, cancellationToken);
            var hash = headResult.ExitCode == 0 ? headResult.Stdout.Trim() : "unknown";
            commits.Add(new CommitExecuted(hash, group.Message, [.. group.Files]));
            CommitCreated?.Invoke(new CommitCreatedEvent(
                request.RepoRoot, request.Inventory.HeadHash, hash, group.Message, [.. group.Files]));

            foreach (var file in group.Files)
            {
                uncommitted.Remove(file);
            }
        }

        return new ExecuteOutcome(true, null,
            new CommitToolDetails(false, commits, request.Inventory.ExcludedFiles, [.. uncommitted]));
    }

    /// <summary>
    /// Rename items stage both the destination and the source path so the index can
    /// pair them (git detects the rename from the staged tree; <c>diff --cached
    /// --name-only</c> then reports only the destination).
    /// </summary>
    private static IReadOnlyList<string> ExpandStagePaths(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, ChangeItem> inventoryByPath)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            paths.Add(file);
            if (inventoryByPath.TryGetValue(file, out var item) && item.IsRename && item.RenameSource is not null)
            {
                paths.Add(item.RenameSource);
            }
        }

        return paths.ToList();
    }

    private static HashSet<string> ParseNameOnly(string output)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in output.Split('\0'))
        {
            if (token.Length > 0)
            {
                paths.Add(token);
            }
        }

        return paths;
    }

    private static IReadOnlyList<string> RemainingPaths(ExecuteRequest request)
        => request.Inventory.Changes.Select(item => item.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
}
