using System.ComponentModel;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Search;

public sealed class GrepTool(IExecutionEnv env) : JsonTool<GrepToolInput, GrepToolDetails?>(ToolSchemas.FromType<GrepToolInput>())
{
    private const int DefaultLimit = 100;
    private readonly IExecutionEnv _env = env;
    private readonly IContentSearchBackend _externalBackend = new RipgrepContentSearchBackend(env);
    private readonly IContentSearchBackend _nativeBackend = new NativeContentSearchBackend(env);

    public override string Name => "grep";
    public override string Label => "grep";
    public override string Description => "Search file contents for a pattern. Uses ripgrep when available, with native filesystem fallback.";
    public override string PromptSnippet => "Search file contents";

    public override async Task<AgentToolResult<GrepToolDetails?>> ExecuteAsync(string toolCallId, GrepToolInput parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<GrepToolDetails?>? onUpdate = null)
    {
        var request = new ContentSearchRequest(
            await PathUtilities.ResolvePathAsync(_env, parameters.Path ?? ".").ConfigureAwait(false),
            parameters.Pattern,
            parameters.Glob,
            parameters.IgnoreCase == true,
            parameters.Literal == true,
            parameters.Context ?? 0,
            parameters.Limit ?? DefaultLimit);
        var result = await _externalBackend.SearchAsync(request, cancellationToken).ConfigureAwait(false)
                     ?? await _nativeBackend.SearchAsync(request, cancellationToken).ConfigureAwait(false)
                     ?? new ContentSearchResult([]);
        return ToResult(result, request.Limit);
    }

    private static AgentToolResult<GrepToolDetails?> ToResult(ContentSearchResult result, int limit)
    {
        var formatted = SearchResultFormatter.FormatContentResults(result, limit);
        return new AgentToolResult<GrepToolDetails?>([new TextContent(formatted.Text)], new GrepToolDetails(formatted.Truncation, result.MatchLimitReached, result.LinesTruncated));
    }

}

public sealed record GrepToolInput(
    [property: Description("Search pattern (regex or literal string)")]
    string Pattern,

    [property: Description("Directory or file to search (default: current directory)")]
    string? Path = null,

    [property: Description("Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'")]
    string? Glob = null,

    [property: Description("Case-insensitive search (default: false)")]
    bool? IgnoreCase = null,

    [property: Description("Treat pattern as literal string instead of regex (default: false)")]
    bool? Literal = null,

    [property: Description("Number of lines to show before and after each match (default: 0)")]
    int? Context = null,

    [property: Description("Maximum number of matches to return (default: 100)")]
    int? Limit = null);
public sealed record GrepToolDetails(TruncationResult? Truncation = null, int? MatchLimitReached = null, bool LinesTruncated = false);
