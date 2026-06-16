namespace PiSharp.Abstractions.Messages;

/// <summary>
/// Base type for messages flowing through the agent system.
/// Provider messages are modeled as sealed records; custom messages remain extensible.
/// </summary>
public abstract record AgentMessage(string Role, DateTimeOffset Timestamp);

public sealed record UserMessage(
    IReadOnlyList<MessageContent> Content,
    DateTimeOffset Timestamp = default) : AgentMessage("user", Timestamp == default ? DateTimeOffset.UtcNow : Timestamp);

public sealed record AssistantMessage(
    IReadOnlyList<MessageContent> Content,
    string? Api = null,
    string? Provider = null,
    string? Model = null,
    UsageInfo? Usage = null,
    string? StopReason = null,
    string? ErrorMessage = null,
    DateTimeOffset Timestamp = default,
    string? ResponseModel = null,
    string? ResponseId = null,
    IReadOnlyList<ProviderDiagnostic>? Diagnostics = null) : AgentMessage("assistant", Timestamp == default ? DateTimeOffset.UtcNow : Timestamp);

public sealed record ToolResultMessage(
    string ToolUseId,
    string ToolName,
    IReadOnlyList<MessageContent> Content,
    object? Details = null,
    bool IsError = false,
    DateTimeOffset Timestamp = default) : AgentMessage("toolResult", Timestamp == default ? DateTimeOffset.UtcNow : Timestamp);

/// <summary>
/// Extensible base for application-defined messages.
/// Downstream packages create sealed records deriving from this type.
/// </summary>
public abstract record CustomAgentMessage(string CustomRole, DateTimeOffset Timestamp)
    : AgentMessage(CustomRole, Timestamp);

public sealed record UsageInfo(
    int Input = 0,
    int Output = 0,
    int CacheRead = 0,
    int CacheWrite = 0,
    int TotalTokens = 0,
    UsageCost? Cost = null);

public sealed record UsageCost(
    decimal Input = 0,
    decimal Output = 0,
    decimal CacheRead = 0,
    decimal CacheWrite = 0,
    decimal Total = 0);

public sealed record ProviderDiagnostic(
    string Type,
    string Message,
    IReadOnlyDictionary<string, object?>? Details = null);

public static class AgentMessages
{
    public static UserMessage User(string text, DateTimeOffset timestamp = default)
        => new([new TextContent(text)], timestamp);

    public static UserMessage User(IEnumerable<MessageContent> content, DateTimeOffset timestamp = default)
        => new(content.ToArray(), timestamp);

    public static AssistantMessage Assistant(string text, DateTimeOffset timestamp = default)
        => new([new TextContent(text)], Timestamp: timestamp);

    public static ToolResultMessage ToolResult(
        string toolUseId,
        string toolName,
        string text,
        object? details = null,
        bool isError = false,
        DateTimeOffset timestamp = default)
        => new(toolUseId, toolName, [new TextContent(text)], details, isError, timestamp);
}
