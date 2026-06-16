using PiSharp.Runtime.Session;
using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class ExportSessionSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["export"];

    public string Description { get; } = "Built-in /export command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(args)
            ? Path.Combine(Path.GetTempPath(), $"pisharp-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.html")
            : Path.GetFullPath(args.Trim());

        if (!context.IsPathUnderAllowedRoot(resolvedPath))
            return new SlashCommandResult(true, "Export path must be under the session or temp directory.", true);

        await HtmlSessionRenderer.ExportToFileAsync(context.Runtime.Session, resolvedPath, cancellationToken);
        return new SlashCommandResult(true, $"Exported session to {resolvedPath}");
    }
}
