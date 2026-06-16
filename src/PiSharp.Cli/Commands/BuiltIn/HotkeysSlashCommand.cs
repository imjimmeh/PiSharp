using PiSharp.Tui.Interactive;
using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class HotkeysSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["hotkeys"];

    public string Description { get; } = "Built-in /hotkeys command";

    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
        => Task.FromResult(new SlashCommandResult(true, TuiKeybindings.HotkeysText()));
}
