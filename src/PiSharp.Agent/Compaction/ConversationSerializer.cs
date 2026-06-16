using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;

namespace PiSharp.Agent.Compaction;

public static class ConversationSerializer
{
    public static string SerializeMessage(AgentMessage message) => message switch
    {
        UserMessage user => $"[User] {GetText(user.Content)}",
        AssistantMessage assistant => $"[Assistant] {GetText(assistant.Content)}" + (assistant.Content.OfType<ToolCallContent>().Any() ? $"\n[Assistant tool calls] {string.Join(", ", assistant.Content.OfType<ToolCallContent>().Select(c => c.Name))}" : ""),
        ToolResultMessage tool => $"[Tool result ({tool.ToolName})] {GetText(tool.Content)}",
        _ => $"[{message.Role}]"
    };

    private static string GetText(IReadOnlyList<MessageContent> content) => string.Join("\n", content.OfType<TextContent>().Select(c => c.Text));
}
