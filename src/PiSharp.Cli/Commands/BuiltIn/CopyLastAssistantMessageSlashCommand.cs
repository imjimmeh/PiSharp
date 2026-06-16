using PiSharp.Abstractions.Messages;
using System.Collections.Immutable;
using System.Linq;

namespace PiSharp.Cli.Commands;

public sealed class CopyLastAssistantMessageSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["copy"];

    public string Description { get; } = "Built-in /copy command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var sessionContext = await context.Runtime.Session.BuildContextAsync(cancellationToken);
        var text = string.Concat(
            sessionContext.Messages.OfType<AssistantMessage>().LastOrDefault()?
                .Content.OfType<TextContent>().Select(content => content.Text) ?? []);
        if (string.IsNullOrEmpty(text))
            return new SlashCommandResult(true, "No assistant message to copy.", true);
        return new SlashCommandResult(true, "Last assistant message copied.\n\n" + text);
    }
}
