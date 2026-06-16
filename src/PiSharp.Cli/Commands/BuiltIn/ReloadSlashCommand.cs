using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class ReloadSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["reload"];

    public string Description { get; } = "Built-in /reload command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        await context.Runtime.ReloadExtensionsAsync(cancellationToken);
        return new SlashCommandResult(true, "Extensions reloaded.");
    }
}
