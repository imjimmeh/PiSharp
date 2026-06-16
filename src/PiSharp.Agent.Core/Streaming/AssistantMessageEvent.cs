using PiSharp.Abstractions.Messages;

namespace PiSharp.Agent.Core.Streaming;

public abstract record AssistantMessageEvent(AssistantMessage Partial)
{
    public sealed record Start(AssistantMessage Partial) : AssistantMessageEvent(Partial);

    public sealed record TextStart(
        AssistantMessage Partial,
        int ContentIndex) : AssistantMessageEvent(Partial);

    public sealed record TextDelta(
        AssistantMessage Partial,
        int ContentIndex,
        string Delta) : AssistantMessageEvent(Partial);

    public sealed record TextEnd(
        AssistantMessage Partial,
        int ContentIndex) : AssistantMessageEvent(Partial);

    public sealed record ThinkingStart(
        AssistantMessage Partial,
        int ContentIndex) : AssistantMessageEvent(Partial);

    public sealed record ThinkingDelta(
        AssistantMessage Partial,
        int ContentIndex,
        string Delta) : AssistantMessageEvent(Partial);

    public sealed record ThinkingEnd(
        AssistantMessage Partial,
        int ContentIndex) : AssistantMessageEvent(Partial);

    public sealed record ToolCallStart(
        AssistantMessage Partial,
        int ContentIndex) : AssistantMessageEvent(Partial);

    public sealed record ToolCallDelta(
        AssistantMessage Partial,
        int ContentIndex,
        string Delta) : AssistantMessageEvent(Partial);

    public sealed record ToolCallEnd(
        AssistantMessage Partial,
        int ContentIndex,
        ToolCallContent ToolCall) : AssistantMessageEvent(Partial);

    public sealed record Done(
        AssistantMessage Message,
        string? Reason = null) : AssistantMessageEvent(Message);

    public sealed record Error(
        AssistantMessage ErrorMessage,
        string? Reason = null) : AssistantMessageEvent(ErrorMessage);
}
