using System.Collections.Immutable;
using System.Text;

namespace PiSharp.Cli.Commands;

public sealed class ScopedModelsSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["scoped-models"];

    public string Description { get; } = "Built-in /scoped-models command";

    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var current = context.Runtime.CurrentModelSelection;
        if (!current.IsScoped || current.ScopedModels.Count == 0)
            return Task.FromResult(new SlashCommandResult(true, "No scoped models configured. Use --models flag."));

        var builder = new StringBuilder();
        builder.AppendLine("Scoped models:");
        foreach (var model in current.ScopedModels)
            builder.AppendLine($"  {model.Provider}/{model.Id}");
        return Task.FromResult(new SlashCommandResult(true, builder.ToString().TrimEnd()));
    }
}
