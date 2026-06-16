using System.ComponentModel;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Search;

public sealed class FindTool(IExecutionEnv env) : JsonTool<FindToolInput, FindToolDetails?>(ToolSchemas.FromType<FindToolInput>())
{
    private const int DefaultLimit = 1000;
    private readonly IExecutionEnv _env = env;
    private readonly IFileSearchBackend _externalBackend = new FdFileSearchBackend(env);
    private readonly IFileSearchBackend _nativeBackend = new NativeFileSearchBackend(env);

    public override string Name => "find";
    public override string Label => "find";
    public override string Description => "Find files by glob pattern. Uses fd when available, with native filesystem fallback.";
    public override string PromptSnippet => "Find files and directories";

    public override async Task<AgentToolResult<FindToolDetails?>> ExecuteAsync(string toolCallId, FindToolInput parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<FindToolDetails?>? onUpdate = null)
    {
        var request = new FileSearchRequest(
            await PathUtilities.ResolvePathAsync(_env, parameters.Path ?? ".").ConfigureAwait(false),
            parameters.Pattern,
            parameters.Limit ?? DefaultLimit);
        var result = await _externalBackend.FindAsync(request, cancellationToken).ConfigureAwait(false)
                     ?? await _nativeBackend.FindAsync(request, cancellationToken).ConfigureAwait(false)
                     ?? new FileSearchResult([]);
        return ToResult(result, request.Limit);
    }

    private static AgentToolResult<FindToolDetails?> ToResult(FileSearchResult result, int limit)
    {
        var formatted = SearchResultFormatter.FormatFileResults(result, limit);
        return new AgentToolResult<FindToolDetails?>([new TextContent(formatted.Text)], new FindToolDetails(formatted.Truncation, result.ResultLimitReached));
    }

}

public sealed record FindToolInput(
    [property: Description("Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'")]
    string Pattern,

    [property: Description("Directory to search in (default: current directory)")]
    string? Path = null,

    [property: Description("Maximum number of results (default: 1000)")]
    int? Limit = null);
public sealed record FindToolDetails(TruncationResult? Truncation = null, int? ResultLimitReached = null);
