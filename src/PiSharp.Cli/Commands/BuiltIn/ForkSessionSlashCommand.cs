using PiSharp.Abstractions.Sessions;
using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class ForkSessionSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["fork", "clone"];

    public string Description { get; } = "Built-in /fork command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var result = await context.Runtime.ForkAsync(context.Runtime.Session.Metadata, new SessionForkOptions(string.IsNullOrWhiteSpace(args) ? null : args), cancellationToken);
        if (result.Cancelled) return new SlashCommandResult(true, context.SessionChangeCancelledMessage("Session fork", result.Reason));
        return new SlashCommandResult(true, "Forked session.");
    }
}
