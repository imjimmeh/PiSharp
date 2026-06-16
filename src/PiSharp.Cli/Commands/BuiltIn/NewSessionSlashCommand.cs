using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class NewSessionSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["new"];

    public string Description { get; } = "Built-in /new command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var result = await context.Runtime.NewSessionAsync(cancellationToken);
        if (result.Cancelled) return new SlashCommandResult(true, context.SessionChangeCancelledMessage("Session creation", result.Reason));
        return new SlashCommandResult(true, "Started a new session.");
    }
}
