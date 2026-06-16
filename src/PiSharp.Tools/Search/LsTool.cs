using System.ComponentModel;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Search;

public sealed class LsTool(IExecutionEnv env) : JsonTool<LsToolInput, LsToolDetails?>(ToolSchemas.FromType<LsToolInput>())
{
    private const int DefaultLimit = 500;
    private readonly IExecutionEnv _env = env;

    public override string Name => "ls";
    public override string Label => "ls";
    public override string Description => "List directory contents. Directories are suffixed with /.";
    public override string PromptSnippet => "List directory contents";

    public override async Task<AgentToolResult<LsToolDetails?>> ExecuteAsync(string toolCallId, LsToolInput parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<LsToolDetails?>? onUpdate = null)
    {
        var limit = parameters.Limit ?? DefaultLimit;
        var path = await PathUtilities.ResolvePathAsync(_env, parameters.Path ?? ".").ConfigureAwait(false);
        var entriesResult = await _env.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = entriesResult.GetOrThrow(error => error)
            .OrderBy(entry => entry.Kind == FileKind.Directory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var limited = entries.Take(limit).Select(entry => entry.Kind == FileKind.Directory ? entry.Name + "/" : entry.Name).ToArray();
        var body = limited.Length == 0 ? "Directory is empty" : string.Join("\n", limited);
        var truncation = Truncation.TruncateHead(body);
        var entryLimitReached = entries.Length > limit ? limit : (int?)null;
        var text = truncation.Content;
        if (entryLimitReached is not null) text += $"\n\n[Showing first {limit} entries of {entries.Length}. Raise limit to see more.]";
        return new AgentToolResult<LsToolDetails?>([new TextContent(text)], new LsToolDetails(truncation.Truncated ? truncation : null, entryLimitReached));
    }

}

public sealed record LsToolInput(
    [property: Description("Directory to list (default: current directory)")]
    string? Path = null,

    [property: Description("Maximum number of entries to return (default: 500)")]
    int? Limit = null);
public sealed record LsToolDetails(TruncationResult? Truncation = null, int? EntryLimitReached = null);
