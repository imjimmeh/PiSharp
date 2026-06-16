using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public interface IBuiltInSlashCommand
{
    ImmutableArray<string> Names { get; }

    string Description { get; }

    Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken);
}
