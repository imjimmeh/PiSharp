using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class CompactSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["compact"];

    public string Description { get; } = "Built-in /compact command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        await context.Runtime.Harness.CompactAsync(string.IsNullOrWhiteSpace(args) ? null : args, cancellationToken);
        return new SlashCommandResult(true, "Compacted session.");
    }
}
