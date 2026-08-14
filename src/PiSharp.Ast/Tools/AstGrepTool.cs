using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ast.Ast;
using PiSharp.Tools;
using PiSharp.Tools.Edit;
using PiSharp.Tools.Search;
using PiSharp.Tools.Shared;

namespace PiSharp.Ast.Tools;

/// <summary>
/// <c>ast_grep</c>: structural search over C# files (Roslyn). Patterns use metavariables
/// (<c>$A</c> = one node, <c>$$$ARGS</c> = a list); trivia is ignored; unsupported languages
/// fail with an explicit error listing the registered providers.
/// </summary>
public sealed class AstGrepTool(IExecutionEnv env, AstLanguageRegistry registry, Func<bool>? isEnabled = null)
    : JsonTool<AstGrepInput, AstGrepDetails?>(ToolSchemas.FromType<AstGrepInput>())
{
    internal const int DefaultLimit = 100;
    private readonly IExecutionEnv _env = env;
    private readonly AstLanguageRegistry _registry = registry;
    private readonly Func<bool>? _isEnabled = isEnabled;

    public override string Name => "ast_grep";
    public override string Label => "ast_grep";
    public override string Description =>
        "Search C# files by AST structure. Pattern metavariables: $A matches one node, $$$ARGS matches a list. " +
        "Trivia (comments/whitespace) is ignored. Unsupported languages fail with a clear error.";
    public override string PromptSnippet => "Search code by parse-tree structure";

    public override async Task<AgentToolResult<AstGrepDetails?>> ExecuteAsync(
        string toolCallId,
        AstGrepInput parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<AstGrepDetails?>? onUpdate = null)
    {
        if (_isEnabled is not null && !_isEnabled())
        {
            return new AgentToolResult<AstGrepDetails?>([new TextContent("pisharp-structural-editing.ast.enabled is false")], null);
        }

        var limit = parameters.Limit ?? DefaultLimit;
        if (limit < 0)
        {
            throw new InvalidOperationException("limit must be greater than or equal to 0.");
        }

        var rootPath = await PathUtilities.ResolvePathAsync(_env, parameters.Path ?? _env.Cwd).ConfigureAwait(false);
        var info = await _env.GetFileInfoAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (!info.IsOk)
        {
            throw info.Error;
        }
        var singleFile = info.Value.Kind == FileKind.File;
        var files = singleFile ? [info.Value] : await EnumerateFilesAsync(rootPath, cancellationToken).ConfigureAwait(false);

        var matches = new List<AstMatch>();
        foreach (var file in files)
        {
            if (matches.Count >= limit) break;

            var relative = RelativePathFormatter.Format(rootPath, file.Path);
            if (parameters.Glob is not null && !GlobMatcher.IsMatch(parameters.Glob, relative)) continue;

            var provider = _registry.Resolve(parameters.Language, file.Path);
            if (provider is null)
            {
                // Explicit language override, or a single-file target, that no provider handles
                // must fail loudly — never a silent no-op.
                if (parameters.Language is not null || singleFile)
                {
                    throw new InvalidOperationException(_registry.UnsupportedLanguageMessage(parameters.Language, file.Path));
                }
                continue;
            }

            var read = await _env.ReadTextFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!read.IsOk) continue;
            var content = EditDiff.StripBom(read.Value).Text;

            SyntaxNode root;
            try
            {
                root = (SyntaxNode)provider.Parse(content, file.Path).Root;
            }
            catch (AstParseException)
            {
                continue; // unparseable file — no structural matches, mirroring grep's skip of unreadable files
            }

            var found = provider.FindMatches(root, parameters.Pattern, content, file.Path, limit - matches.Count);
            matches.AddRange(found);
        }

        var rendered = Render(rootPath, matches);
        var truncation = Truncation.TruncateHead(rendered);
        var output = truncation.Truncated
            ? truncation.Content + "\n[Truncated: output exceeds " + truncation.MaxLines + " lines or " + truncation.MaxBytes + " bytes; matches above are complete]"
            : truncation.Content;

        return new AgentToolResult<AstGrepDetails?>(
            [new TextContent(output)],
            new AstGrepDetails(matches, truncation, matches.Count >= limit));
    }

    private static string Render(string rootPath, IReadOnlyList<AstMatch> matches)
    {
        var sb = new StringBuilder();
        foreach (var match in matches)
        {
            sb.Append(match.Path).Append(':')
              .Append(match.Line).Append(':').Append(match.Column)
              .Append('-').Append(match.EndLine).Append(':').Append(match.EndColumn)
              .Append(": ").AppendLine(match.Text);
            if (match.Captures is not null)
            {
                foreach (var (name, text) in match.Captures)
                {
                    sb.Append("  $").Append(name).Append(": ").AppendLine(text);
                }
            }
        }
        return sb.ToString();
    }

    private async Task<IReadOnlyList<Abstractions.Environment.FileSystemInfo>> EnumerateFilesAsync(string path, CancellationToken cancellationToken)
    {
        var info = await _env.GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsOk && info.Value.Kind == FileKind.File) return [info.Value];
        var entries = await _env.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        if (!entries.IsOk) return [];
        var files = new List<Abstractions.Environment.FileSystemInfo>();
        foreach (var entry in entries.Value)
        {
            if (entry.Kind == FileKind.File) files.Add(entry);
            else if (entry.Kind == FileKind.Directory)
                files.AddRange(await EnumerateFilesAsync(entry.Path, cancellationToken).ConfigureAwait(false));
        }
        return files;
    }
}

public sealed record AstGrepInput(
    [property: Description("Structural pattern with metavariables ($A, $$$ARGS). Must parse as a single node.")]
    string Pattern,
    [property: Description("File or directory to search (default: current directory).")]
    string? Path = null,
    [property: Description("Filter files by glob pattern, e.g. '**/*.cs'.")]
    string? Glob = null,
    [property: Description("Language override ('csharp'). Defaults to auto-detection from file extension.")]
    string? Language = null,
    [property: Description("Maximum number of matches to return (default: 100).")]
    int? Limit = null);

public sealed record AstGrepDetails(
    IReadOnlyList<AstMatch> Matches,
    TruncationResult? Truncation = null,
    bool MatchLimitReached = false);
