using System.Text.Json;

namespace PiSharp.Abstractions.Messages;

public abstract record MessageContent;

public sealed record TextContent(
    string Text,
    string? TextSignature = null) : MessageContent;

public sealed record ImageContent(
    string MediaType,
    string Data) : MessageContent;

public sealed record ThinkingContent(
    string Thinking,
    string? ThinkingSignature = null,
    bool Redacted = false) : MessageContent;

public sealed record ToolCallContent(
    string Id,
    string Name,
    JsonElement Arguments,
    string? ThoughtSignature = null) : MessageContent;
