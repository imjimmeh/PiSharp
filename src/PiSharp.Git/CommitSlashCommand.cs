using PiSharp.Extensions;

namespace PiSharp.Git;
/// <summary>
/// The <c>/commit</c> slash command. Fully deterministic for the user: it captures the
/// inventory, auto-groups by (category, directory), auto-drafts Conventional-Commits-style
/// messages, prints the plan, asks for per-group confirmation/editing, then executes the
/// shared <see cref="CommitExecutor"/>.
///
/// Grammar: <c>/commit [--yes] [--dry-run] [--no-split] [--message &lt;text&gt;] [--files &lt;paths...&gt;]</c>.
/// </summary>
public sealed class CommitSlashCommand(
    CommandHost host,
    CommitInventoryService inventoryService,
    CommitPlanner planner,
    CommitExecutor executor,
    GitPluginOptions options)
{
    public async Task HandleAsync(string args, CancellationToken cancellationToken = default)
    {
        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            await NotifyAsync("Usage: /commit [--yes] [--dry-run] [--no-split] [--message <text>] [--files <paths...>]", true);
            return;
        }

        // --message without --no-split is a usage error.
        if (parsed.Message is not null && !parsed.NoSplit)
        {
            await NotifyAsync("--message requires --no-split (single-commit mode).", true);
            return;
        }

        var captureOptions = new CommitInventoryService.CaptureOptions(
            ScopeFiles: parsed.Files,
            IncludeStaged: true,
            IncludeUnstaged: true);
        var capture = await inventoryService.CaptureAsync(host.Cwd, captureOptions, cancellationToken);
        if (!capture.Success)
        {
            await NotifyAsync(capture.Error ?? "Could not capture the change inventory.", true);
            return;
        }

        var inventory = capture.Inventory!;
        if (inventory.Changes.Count == 0)
        {
            await NotifyAsync("Nothing to commit.", false);
            return;
        }

        // Build the plan.
        List<CommitGroupInput> groups;
        if (parsed.NoSplit)
        {
            var message = parsed.Message ?? DraftSingleMessage(inventory);
            groups = [new CommitGroupInput { Message = message, Files = inventory.Changes.Select(i => i.Path).ToList() }];
        }
        else
        {
            var plan = planner.Plan(inventory, options.CommitConventionalPrefix);
            if (!plan.Success)
            {
                await NotifyAsync(plan.Error ?? "Could not build a commit plan.", true);
                return;
            }

            groups = plan.Groups!
                .Select((group, index) => new CommitGroupInput
                {
                    Id = index.ToString(),
                    Message = group.Message,
                    Files = group.Files,
                    DependsOn = group.DependsOn.Select(d => d.ToString()).ToList()
                })
                .ToList();
        }

        // Print the plan (persistent chat row + status line).
        var planLines = groups.Select((group, index) => $"{index + 1}/{groups.Count}: {group.Message} [{group.Files.Count} files]");
        var planText = "Commit plan:\n" + string.Join("\n", planLines);
        await host.SendMessageAsync(planText, cancellationToken);
        await NotifyAsync($"Planned {groups.Count} commit{(groups.Count == 1 ? string.Empty : "s")}.", false);

        if (parsed.DryRun)
        {
            await NotifyAsync("Dry run — no commits created.", false);
            return;
        }

        // Per-group confirmation + message editing.
        var confirmRequired = options.CommitConfirmSlashCommand && host.HasUi && !parsed.Yes;
        if (confirmRequired)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                var ok = await host.Ui.ConfirmAsync($"Commit {i + 1}/{groups.Count}: {groups[i].Message} [{groups[i].Files.Count} files]?", cancellationToken);
                if (!ok)
                {
                    await NotifyAsync("Commit cancelled by user.", false);
                    return;
                }

                var edited = await host.Ui.InputAsync("Commit message:", groups[i].Message, cancellationToken);
                if (!string.IsNullOrWhiteSpace(edited))
                {
                    groups[i] = groups[i] with { Message = edited };
                }
            }
        }

        var outcome = await executor.ExecuteAsync(new CommitExecutor.ExecuteRequest(
            capture.RepoRoot!,
            inventory,
            groups,
            RunHooks: options.CommitRunHooks), cancellationToken);

        if (!outcome.Success)
        {
            await NotifyAsync(outcome.Error ?? "Commit failed.", true);
            return;
        }

        var details = outcome.Details!;
        var summary = new List<string> { $"Committed {details.Commits.Count} change{(details.Commits.Count == 1 ? string.Empty : "s")}." };
        foreach (var commit in details.Commits)
        {
            summary.Add($"  {commit.Hash[..Math.Min(7, commit.Hash.Length)]} {commit.Message} ({commit.Files.Count} files)");
        }

        if (details.ExcludedFiles.Count > 0)
        {
            summary.Add($"Excluded (never auto-committed): {string.Join(", ", details.ExcludedFiles)}");
        }

        if (details.RemainingFiles.Count > 0)
        {
            summary.Add($"Remaining (not committed): {string.Join(", ", details.RemainingFiles)}");
        }

        await host.SendMessageAsync(string.Join("\n", summary), cancellationToken);
        await NotifyAsync("Commit completed.", false);
    }

    private static string DraftSingleMessage(ChangeInventory inventory)
    {
        var dominant = inventory.Changes
            .GroupBy(item => item.Category)
            .OrderByDescending(g => g.Count())
            .First().Key;
        return ChangeCategoryToType(dominant) + ": working tree changes";
    }

    private static string ChangeCategoryToType(ChangeCategory category)
        => category switch
        {
            ChangeCategory.Source => "feat",
            ChangeCategory.Test => "test",
            ChangeCategory.Docs => "docs",
            _ => "chore"
        };

    private async Task NotifyAsync(string message, bool isError)
    {
        try
        {
            await host.Ui.NotifyAsync(message, isError ? ExtensionUiSeverity.Error : ExtensionUiSeverity.Success);
        }
        catch (NotSupportedException)
        {
            // No UI (print/rpc): output rides the command result instead.
        }
    }

    private static ParsedArgs? ParseArgs(string args)
    {
        var tokens = Tokenize(args);
        var yes = false;
        var dryRun = false;
        var noSplit = false;
        string? message = null;
        var files = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case "--yes":
                    yes = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-split":
                    noSplit = true;
                    break;
                case "--message":
                    if (i + 1 >= tokens.Count)
                    {
                        return null;
                    }

                    message = tokens[++i];
                    break;
                case "--files":
                    // All remaining tokens are scope paths.
                    files.AddRange(tokens.Skip(i + 1));
                    i = tokens.Count;
                    break;
                default:
                    return null; // unknown token
            }
        }

        return new ParsedArgs(yes, dryRun, noSplit, message, files.Count > 0 ? files : null);
    }

    private static IReadOnlyList<string> Tokenize(string args)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in (args ?? string.Empty).Trim())
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private sealed record ParsedArgs(bool Yes, bool DryRun, bool NoSplit, string? Message, IReadOnlyList<string>? Files);
}
