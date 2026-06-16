using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;

namespace PiSharp.Ai.Providers.Shared;

public sealed record ProviderMessage(string Role, IReadOnlyList<ProviderContent> Content);

public sealed record ProviderContent(
    string Type,
    string? Text = null,
    string? MediaType = null,
    string? Data = null,
    string? Id = null,
    string? Name = null,
    JsonElement? Arguments = null,
    string? ToolUseId = null,
    bool IsError = false,
    string? Signature = null);

public static class MessageTransformer
{
    public static IReadOnlyList<ProviderMessage> ToProviderMessages(
        AgentContext context,
        bool supportsImages = true,
        bool flattenThinking = false,
        bool insertSyntheticToolResults = true,
        bool includeSystemPrompt = true)
    {
        var messages = new List<ProviderMessage>();
        var pendingToolCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        var existingToolResultIds = new HashSet<string>(StringComparer.Ordinal);

        if (includeSystemPrompt && !string.IsNullOrWhiteSpace(context.SystemPrompt))
        {
            messages.Add(new ProviderMessage("system", [new ProviderContent("text", Text: context.SystemPrompt)]));
        }

        foreach (var message in context.Messages)
        {
            switch (message)
            {
                case UserMessage user:
                    messages.Add(new ProviderMessage("user", TransformContent(user.Content, supportsImages, flattenThinking, pendingToolCalls)));
                    break;
                case AssistantMessage { ErrorMessage: not null }:
                    break;
                case AssistantMessage assistant:
                    messages.Add(new ProviderMessage("assistant", TransformContent(assistant.Content, supportsImages, flattenThinking, pendingToolCalls)));
                    break;
                case ToolResultMessage toolResult:
                    if (!pendingToolCalls.ContainsKey(toolResult.ToolUseId))
                        throw new InvalidOperationException($"Cannot transform ToolResultMessage for tool call '{toolResult.ToolUseId}' because no prior assistant tool call exists in the provider context.");
                    existingToolResultIds.Add(toolResult.ToolUseId);
                    messages.Add(new ProviderMessage("tool", [new ProviderContent(
                        "tool_result",
                        Text: string.Join("\n", toolResult.Content.OfType<TextContent>().Select(text => text.Text)),
                        ToolUseId: ToolTransformer.NormalizeToolCallId(toolResult.ToolUseId),
                        Name: toolResult.ToolName,
                        IsError: toolResult.IsError)]));
                    break;
            }
        }

        if (insertSyntheticToolResults)
        {
            foreach (var pending in pendingToolCalls)
            {
                if (existingToolResultIds.Contains(pending.Key)) continue;
                messages.Add(new ProviderMessage("tool", [new ProviderContent(
                    "tool_result",
                    Text: string.Empty,
                    ToolUseId: ToolTransformer.NormalizeToolCallId(pending.Key),
                    Name: pending.Value)]));
            }
        }

        return messages;
    }

    private static IReadOnlyList<ProviderContent> TransformContent(
        IReadOnlyList<MessageContent> content,
        bool supportsImages,
        bool flattenThinking,
        IDictionary<string, string> pendingToolCalls)
    {
        var result = new List<ProviderContent>();
        foreach (var item in content)
        {
            switch (item)
            {
                case TextContent text:
                    result.Add(new ProviderContent("text", Text: text.Text, Signature: text.TextSignature));
                    break;
                case ImageContent image when supportsImages:
                    result.Add(new ProviderContent("image", MediaType: image.MediaType, Data: image.Data));
                    break;
                case ImageContent image:
                    result.Add(new ProviderContent("text", Text: $"[unsupported image: {image.MediaType}]"));
                    break;
                case ThinkingContent thinking when flattenThinking:
                    result.Add(new ProviderContent("text", Text: thinking.Thinking, Signature: thinking.ThinkingSignature));
                    break;
                case ThinkingContent thinking:
                    result.Add(new ProviderContent("thinking", Text: thinking.Thinking, Signature: thinking.ThinkingSignature));
                    break;
                case ToolCallContent toolCall:
                    var normalizedId = ToolTransformer.NormalizeToolCallId(toolCall.Id);
                    pendingToolCalls[toolCall.Id] = toolCall.Name;
                    result.Add(new ProviderContent("tool_call", Id: normalizedId, Name: toolCall.Name, Arguments: toolCall.Arguments.Clone(), Signature: toolCall.ThoughtSignature));
                    break;
            }
        }

        return result;
    }
}
