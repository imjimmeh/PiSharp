using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class QuitSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["quit"];

    public string Description { get; } = "Built-in /quit command";

    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
        => Task.FromResult(new SlashCommandResult(true, ShouldExit: true));
}
