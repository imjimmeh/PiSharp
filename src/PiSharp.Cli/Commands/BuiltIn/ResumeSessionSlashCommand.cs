using PiSharp.Abstractions.Sessions;
using System.Collections.Immutable;
using System.Linq;

namespace PiSharp.Cli.Commands;

public sealed class ResumeSessionSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["resume", "session"];

    public string Description { get; } = "Built-in /resume command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        JsonlSessionMetadata? metadata;
        if (string.IsNullOrWhiteSpace(args) && context.SelectSessionMetadataAsync is not null)
        {
            metadata = await context.SelectSessionMetadataAsync(
                ct => context.Runtime.ListSessionsAsync(context.Runtime.Session.Metadata.Cwd, ct),
                ct => context.Runtime.ListSessionsAsync(null, ct),
                context.Runtime.Session.Metadata,
                cancellationToken);
            if (metadata is null) return new SlashCommandResult(true, "Resume cancelled.");
        }
        else
        {
            var allSessions = await context.Runtime.ListSessionsAsync(null, cancellationToken);
            if (allSessions.Count == 0) return new SlashCommandResult(true, "No sessions found.", true);
            if (string.IsNullOrWhiteSpace(args))
            {
                var selected = await context.SelectAsync("Select session", allSessions.Select(session => $"{session.Id} {session.Path}").ToArray(), cancellationToken);
                if (string.IsNullOrWhiteSpace(selected)) return new SlashCommandResult(true, "Resume cancelled.");
                metadata = FindSession(allSessions, selected) ?? allSessions.FirstOrDefault(session => selected.Contains(session.Id, StringComparison.Ordinal));
            }
            else
            {
                metadata = FindSession(allSessions, args.Trim());
            }
        }

        if (metadata is null) return new SlashCommandResult(true, $"Session '{args.Trim()}' was not found.", true);
        var result = await context.Runtime.SwitchSessionAsync(metadata, cancellationToken);
        if (result.Cancelled) return new SlashCommandResult(true, context.SessionChangeCancelledMessage("Session switch", result.Reason));
        return new SlashCommandResult(true, $"Switched to session {metadata.Id}.");
    }

    private static JsonlSessionMetadata? FindSession(IReadOnlyList<JsonlSessionMetadata> sessions, string selected)
        => sessions.FirstOrDefault(session => string.Equals(session.Id, selected, StringComparison.OrdinalIgnoreCase) || string.Equals(session.Path, selected, StringComparison.OrdinalIgnoreCase));
}
