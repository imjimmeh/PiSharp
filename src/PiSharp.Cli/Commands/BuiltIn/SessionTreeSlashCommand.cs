using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class SessionTreeSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["tree"];

    public string Description { get; } = "Built-in /tree command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(args)) await context.Runtime.Harness.NavigateTreeAsync(args, summarize: false, cancellationToken);
        return new SlashCommandResult(true, "Session tree command handled.");
    }
}
