using System.ComponentModel;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Files;

public sealed class WriteTool(IExecutionEnv env) : JsonTool<WriteToolInput, object?>(ToolSchemas.FromType<WriteToolInput>())
{
    private readonly IExecutionEnv _env = env;

    public override string Name => "write";
    public override string Label => "write";
    public override string Description => "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.";
    public override string PromptSnippet => "Create or overwrite files";
    public override IReadOnlyList<string> PromptGuidelines => ["Use write only for new files or complete rewrites."];

    public override async Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, WriteToolInput parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
        => await FileMutationQueue.RunAsync(_env, parameters.Path, async () =>
        {
            var absolutePath = await PathUtilities.ResolvePathAsync(_env, parameters.Path).ConfigureAwait(false);
            await PathUtilities.CreateParentDirectoryAsync(_env, absolutePath, cancellationToken).ConfigureAwait(false);
            var write = await _env.WriteFileAsync(absolutePath, parameters.Content, cancellationToken).ConfigureAwait(false);
            write.GetOrThrow(error => error);
            return new AgentToolResult<object?>([new TextContent($"Successfully wrote {parameters.Content.Length} bytes to {parameters.Path}")], null);
        }).ConfigureAwait(false);

}

public sealed record WriteToolInput(
    [property: Description("Path to the file to write (relative or absolute)")]
    string Path,

    [property: Description("Content to write to the file")]
    string Content);
