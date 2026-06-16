using PiSharp.Abstractions.Messages;

namespace PiSharp.Agent.Messages;

public static class MessageConverter
{
    public static IReadOnlyList<AgentMessage> ConvertToLlm(IEnumerable<AgentMessage> messages)
    {
        var converted = new List<AgentMessage>();
        foreach (var message in messages)
        {
            var next = ConvertOne(message);
            if (next is not null)
            {
                converted.Add(next);
            }
        }

        return converted;
    }

    private static AgentMessage? ConvertOne(AgentMessage message)
        => message switch
        {
            UserMessage or AssistantMessage or ToolResultMessage => message,
            BashExecutionMessage bash when bash.ExcludeFromContext => null,
            BashExecutionMessage bash => AgentMessages.User(HarnessMessages.BashExecutionToText(bash), bash.Timestamp),
            CustomMessage custom => new UserMessage(CustomContent(custom), custom.Timestamp),
            BranchSummaryMessage branch => AgentMessages.User(
                HarnessMessageText.BranchSummaryPrefix + branch.Summary + HarnessMessageText.BranchSummarySuffix,
                branch.Timestamp),
            CompactionSummaryMessage compaction => AgentMessages.User(
                HarnessMessageText.CompactionSummaryPrefix + compaction.Summary + HarnessMessageText.CompactionSummarySuffix,
                compaction.Timestamp),
            _ => null
        };

    private static IReadOnlyList<MessageContent> CustomContent(CustomMessage custom)
    {
        if (custom.ContentBlocks is not null)
        {
            return custom.ContentBlocks;
        }

        return [new TextContent(custom.TextContent ?? string.Empty)];
    }
}
