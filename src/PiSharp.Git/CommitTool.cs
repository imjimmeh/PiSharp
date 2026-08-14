using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Git;

/// <summary>
/// The model-facing <c>commit</c> tool. Two-call contract: call 1 with no <c>groups</c>
/// returns the authoritative change inventory (nothing committed); call 2 supplies
/// <c>groups</c> and validates, dependency-orders, and commits each group atomically.
/// A single-call mode (<c>message</c> + <c>split: false</c>) makes one atomic commit.
/// </summary>
public sealed class CommitTool(
    CommitInventoryService inventory,
    CommitExecutor executor,
    GitPluginOptions options)
{
    public string Cwd { get; set; } = ".";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public const string Name = "commit";

    public static string Description =>
        "Inspect and commit working-tree changes. Call once with no 'groups' to get the authoritative " +
        "change inventory (status, category, score, rename pairs). Then call again with 'groups' that " +
        "partition every change into dependency-ordered atomic commits. A dependency cycle is rejected " +
        "with the concrete cycle path. Lockfiles/generated files are excluded and reported, never committed.";

    public sealed record Result(
        AgentToolResult<object?> Output,
        ChangeInventory? Inventory = null,
        CommitToolDetails? Details = null);

    public Task<Result> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(parameters, cancellationToken);

    /// <summary>Host-facing adapter matching the extension tool delegate (returns the result).</summary>
    public async Task<AgentToolResult<object?>> ExecuteForHostAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCoreAsync(parameters, cancellationToken);
        return result.Output;
    }

    public async Task<Result> ExecuteCoreAsync(JsonElement parameters, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.Deserialize<CommitToolInput>(parameters.GetRawText(), SerializerOptions)
            ?? throw new InvalidOperationException("Could not deserialize commit tool arguments.");

        // Semantic validation of the input contract.
        var semanticError = ValidateInput(input);
        if (semanticError is not null)
        {
            return new Result(ErrorResult(semanticError));
        }

        var captureOptions = new CommitInventoryService.CaptureOptions(
            input.Files,
            input.Exclude,
            input.IncludeStaged,
            input.IncludeUnstaged);

        var capture = await inventory.CaptureAsync(Cwd, captureOptions, cancellationToken);
        if (!capture.Success)
        {
            return new Result(ErrorResult(capture.Error ?? "Could not capture the change inventory."));
        }

        if (input.Groups is null && input.Message is null)
        {
            var text = FormatInventory(capture.Inventory!);
            return new Result(
                new AgentToolResult<object?>([new TextContent(text)], capture.Inventory!),
                Inventory: capture.Inventory);
        }

        // Single-commit mode (Message + Split=false): one atomic commit of every captured change.
        var groups = input.Groups ?? [new CommitGroupInput
        {
            Message = input.Message!,
            Files = capture.Inventory!.Changes.Select(item => item.Path).ToList()
        }];

        // Execute call — validate, order, commit.
        var execute = await executor.ExecuteAsync(new CommitExecutor.ExecuteRequest(
            capture.RepoRoot!,
            capture.Inventory!,
            groups,
            RunHooks: options.CommitRunHooks,
            DryRun: input.DryRun), cancellationToken);

        if (!execute.Success)
        {
            return new Result(ErrorResult(execute.Error ?? "Commit failed."), Details: execute.Details);
        }

        var details = execute.Details!;
        var summary = FormatExecution(details);
        var content = details.RejectedCycle is not null
            ? new AgentToolResult<object?>([new TextContent(summary)], details)
            : new AgentToolResult<object?>([new TextContent(summary)], details);
        return new Result(content, Details: details);
    }

    private string? ValidateInput(CommitToolInput input)
    {
        if (input.Groups is not null && !input.Split)
        {
            return "Split=false with Groups is unsupported. Use Groups for dependency-ordered splitting, " +
                   "or a single Message with Split=false for one commit.";
        }

        if (input.Groups is null && input.Message is not null && input.Split)
        {
            return "Message requires Split=false (single-commit mode). Omit Message and supply Groups to split.";
        }

        if (input.Groups is null && input.Message is null && !input.Split)
        {
            return "Provide a Message for single-commit mode, or Groups to execute a split commit.";
        }

        return null;
    }

    private static string FormatInventory(ChangeInventory inventory)
    {
        if (inventory.Changes.Count == 0)
        {
            return "No changes to commit.";
        }

        var lines = new List<string>
        {
            $"Branch: {inventory.Branch ?? "(detached HEAD)"}  HEAD: {inventory.HeadHash ?? "(no commits)"}",
            $"{inventory.Changes.Count} change(s) ({inventory.ExcludedFiles.Count} excluded):"
        };
        foreach (var item in inventory.Changes)
        {
            var rename = item.IsRename ? $" (renamed from {item.RenameSource})" : string.Empty;
            lines.Add($"  [{item.Status}] score={item.Score} {item.Category}: {item.Path}{rename}");
        }

        if (inventory.ExcludedFiles.Count > 0)
        {
            lines.Add($"Excluded (never auto-committed): {string.Join(", ", inventory.ExcludedFiles)}");
        }

        lines.Add("To commit, call again with 'groups' partitioning every change into atomic commits. " +
                  "Use 'id' for dependsOn references, set 'message' per group. A cycle is rejected with its path.");
        return string.Join("\n", lines);
    }

    private static string FormatExecution(CommitToolDetails details)
    {
        var lines = new List<string>();
        if (details.WasDryRun)
        {
            lines.Add("Dry run — no commits created. Planned order:");
            lines.AddRange(details.Commits.Select((c, i) => $"  {i + 1}. {c.Message} ({c.Files.Count} files)"));
        }
        else if (details.RejectedCycle is not null)
        {
            lines.Add($"Dependency cycle rejected: {string.Join(" -> ", details.RejectedCycle)}. No commits created.");
        }
        else if (details.Commits.Count == 0)
        {
            lines.Add("No commits created.");
        }
        else
        {
            lines.Add($"Created {details.Commits.Count} commit(s) in dependency order:");
            foreach (var commit in details.Commits)
            {
                lines.Add($"  {commit.Hash[..Math.Min(7, commit.Hash.Length)]} {commit.Message} ({commit.Files.Count} files)");
            }
        }

        if (details.ExcludedFiles.Count > 0)
        {
            lines.Add($"Excluded (never auto-committed): {string.Join(", ", details.ExcludedFiles)}");
        }

        if (details.RemainingFiles.Count > 0)
        {
            lines.Add($"Remaining (not committed): {string.Join(", ", details.RemainingFiles)}");
        }

        return string.Join("\n", lines);
    }

    private static AgentToolResult<object?> ErrorResult(string message)
        => new([new TextContent(message)], null);

}
