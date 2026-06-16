using System.Collections.Immutable;
using System.Text;

namespace PiSharp.Cli.Commands;

public sealed class ChangelogSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["changelog"];

    public string Description { get; } = "Built-in /changelog command";

    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        var path = ChangelogParser.FindChangelogPath();
        if (path is null)
            return Task.FromResult(new SlashCommandResult(true, "Changelog not available. Check https://github.com/anomalyco/PiSharp/releases for release notes."));

        var markdown = File.ReadAllText(path);
        var entries = ChangelogParser.Parse(markdown);
        if (entries.Count == 0)
            return Task.FromResult(new SlashCommandResult(true, "No changelog entries found.", IsError: true));

        var maxEntries = Math.Min(entries.Count, 5);
        var builder = new StringBuilder();
        for (var i = 0; i < maxEntries; i++)
        {
            if (i > 0) builder.AppendLine();
            builder.AppendLine(entries[i].Content);
        }

        if (entries.Count > maxEntries)
            builder.AppendLine($"\n... and {entries.Count - maxEntries} more versions.");

        return Task.FromResult(new SlashCommandResult(true, builder.ToString().TrimEnd()));
    }
}
