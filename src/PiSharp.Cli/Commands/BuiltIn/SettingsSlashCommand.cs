using System.Collections.Immutable;
using System.Text;

namespace PiSharp.Cli.Commands;

public sealed class SettingsSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["settings"];

    public string Description { get; } = "Built-in /settings command";

    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var model = context.Runtime.CurrentModelSelection.Model;
        var thinking = context.Runtime.Harness.ThinkingLevel;

        var builder = new StringBuilder();
        builder.AppendLine("Current settings:");
        builder.AppendLine($"  Provider: {model.Provider}");
        builder.AppendLine($"  Model: {model.Id}");
        builder.AppendLine($"  Thinking: {thinking}");
        builder.AppendLine($"  Session: {context.Runtime.Session.Metadata.Id}");

        return Task.FromResult(new SlashCommandResult(true, builder.ToString()));
    }
}
