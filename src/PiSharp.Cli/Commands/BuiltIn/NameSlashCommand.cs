using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class NameSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["name"];

    public string Description { get; } = "Built-in /name command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args)) return new SlashCommandResult(true, "Usage: /name <session-name>", true);
        var name = args.Trim();
        await context.Runtime.Harness.SetSessionNameAsync(name, cancellationToken);
        return new SlashCommandResult(true, $"Session name set to \"{name}\".");
    }
}
