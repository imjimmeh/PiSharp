using System.Text;

namespace PiSharp.AgentMessaging;

/// <summary>
/// Markdown "Messaging Brief" builder for the before_prompt_render hook.
/// Only injected when there is content; returns null for an empty brief.
/// </summary>
public static class AgentMessagingBriefFormatter
{
    public const string BriefSectionId = "pisharp.agentmessaging.brief";

    /// <summary>
    /// Builds the brief from the sender's family roster and unread inbox
    /// messages, or null when both are empty.
    /// </summary>
    public static string? FormatBrief(IReadOnlyList<AgentInfo>? family = null, IReadOnlyList<AgentMessage>? messages = null)
    {
        var builder = new StringBuilder();

        if (family is { Count: > 0 })
        {
            builder.AppendLine("### Family Agents");
            foreach (var agent in family)
            {
                var name = string.IsNullOrWhiteSpace(agent.Name) ? string.Empty : $" ({agent.Name})";
                var parent = agent.ParentAgentId is null ? string.Empty : $" → parent `{agent.ParentAgentId}`";
                builder.AppendLine($"- `{agent.AgentId}`{name} — {agent.Role ?? "agent"} ({agent.Status.ToString().ToLowerInvariant()}){parent}");
            }
            builder.AppendLine();
        }

        if (messages is { Count: > 0 })
        {
            builder.AppendLine("### Unread Agent Messages");
            foreach (var message in messages)
            {
                builder.AppendLine($"- **{message.FromAgentId}** ({message.Delivery.ToString().ToLowerInvariant()}): {message.Body}");
            }
            builder.AppendLine();
        }

        if (builder.Length == 0)
            return null;

        builder.Insert(0, "## Messaging Brief\n\n");
        return builder.ToString().TrimEnd();
    }
}
