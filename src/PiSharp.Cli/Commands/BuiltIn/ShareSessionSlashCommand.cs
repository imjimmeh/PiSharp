using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class ShareSessionSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["share"];

    public string Description { get; } = "Built-in /share command";

    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args))
            return Task.FromResult(new SlashCommandResult(true, "Usage: /share <target-path>", true));

        var resolvedPath = Path.GetFullPath(args.Trim());
        if (!context.IsPathUnderAllowedRoot(resolvedPath))
            return Task.FromResult(new SlashCommandResult(true, "Share path must be under the session or temp directory.", true));

        var sessionPath = context.Runtime.Session.Metadata.Path;
        if (!File.Exists(sessionPath))
            return Task.FromResult(new SlashCommandResult(true, "Current session file not found.", true));

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath) ?? ".");
        File.Copy(sessionPath, resolvedPath, overwrite: true);
        return Task.FromResult(new SlashCommandResult(true, $"Session shared to {resolvedPath}"));
    }
}
