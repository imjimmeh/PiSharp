using System.Text;

namespace PiSharp.Coordination;

public static class CoordinationBriefFormatter
{
    public const string BriefSectionId = "pisharp.coordination.brief";

    public static string? FormatBrief(
        IReadOnlyList<AgentEntry>? agents = null,
        IReadOnlyList<InboxMessage>? messages = null,
        string? daemonBriefText = null)
    {
        var builder = new StringBuilder();

        if (agents is { Count: > 0 })
        {
            builder.AppendLine("### Known Agents");
            foreach (var agent in agents)
            {
                var label = agent.SessionId is not null
                    ? $"`{agent.AgentId}` (session `{agent.SessionId}`)"
                    : $"`{agent.AgentId}`";
                builder.AppendLine($"- {label}");
            }
            builder.AppendLine();
        }

        if (messages is { Count: > 0 })
        {
            builder.AppendLine("### Unread Messages");
            foreach (var message in messages)
            {
                builder.AppendLine($"- **{message.FromAgentId}**: {message.Body}");
            }
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(daemonBriefText))
        {
            builder.AppendLine(daemonBriefText.TrimEnd());
            builder.AppendLine();
        }

        if (builder.Length == 0)
            return null;

        builder.Insert(0, "## Coordination Context\n\n");
        return builder.ToString().TrimEnd();
    }
}
