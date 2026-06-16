using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class ImportSessionSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["import"];

    public string Description { get; } = "Built-in /import command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args))
            return new SlashCommandResult(true, "Usage: /import <path-to-session-file>", true);

        var resolvedPath = Path.GetFullPath(args.Trim());
        if (!File.Exists(resolvedPath))
            return new SlashCommandResult(true, $"File not found: {resolvedPath}", true);

        var result = await context.Runtime.ImportSessionFileAsync(resolvedPath, cancellationToken);
        if (result.Cancelled)
            return new SlashCommandResult(true, $"Import failed: {result.Reason ?? "unknown error"}", true);
        return new SlashCommandResult(true, $"Imported session {result.Session?.Id}.");
    }
}
