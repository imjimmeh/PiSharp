using System.Text.Json;
using PiSharp.Abstractions.Messages;

namespace PiSharp.Extensions;

public enum ExtensionChatRowType
{
    User,
    Assistant,
    AssistantThinking,
    ToolCall,
    ToolResult,
    System,
    Error,
    Custom,
    BridgeSlot,
    Unknown
}

public enum ExtensionChatRowKind
{
    Normal,
    User,
    Assistant,
    AssistantThinking,
    Custom,
    ToolRunning,
    ToolSucceeded,
    ToolFailed,
    System,
    Error
}

public enum ExtensionChatSpanKind
{
    Text,
    Muted,
    Accent,
    Success,
    Warning,
    Error,
    Code,
    Border,
    Heading,
    Link
}

public enum ExtensionChatRowMaxWidthPolicy
{
    Wrap,
    Clip
}

public sealed record ExtensionChatInteractionTarget(
    string Kind,
    string Id,
    string? SourceId = null,
    string? Action = null,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record ExtensionChatSpan(string Text, ExtensionChatSpanKind Kind = ExtensionChatSpanKind.Text);

public sealed record ExtensionChatRowLayoutHints(
    int? HorizontalPadding = null,
    int? VerticalPadding = null,
    ExtensionChatRowMaxWidthPolicy MaxWidthPolicy = ExtensionChatRowMaxWidthPolicy.Wrap);

public sealed record ExtensionChatRow(
    string Text,
    ExtensionChatRowKind Kind = ExtensionChatRowKind.Normal,
    IReadOnlyList<ExtensionChatSpan>? Spans = null,
    ExtensionChatInteractionTarget? InteractionTarget = null,
    ExtensionChatInteractionTarget? ContextTarget = null,
    ExtensionChatRowLayoutHints? Layout = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ExtensionChatThemeToken(string Name, string? Value = null);

public sealed record ExtensionChatRowRenderContext(
    ExtensionChatRowType RowType,
    string Role,
    string Text,
    IReadOnlyList<MessageContent>? Content = null,
    string? ToolName = null,
    string? ToolCallId = null,
    JsonElement? ToolArguments = null,
    object? ToolResult = null,
    string? EntryId = null,
    bool IsStreaming = false,
    bool IsError = false,
    bool IsExpanded = false,
    int Width = 100,
    bool ShowThinking = false,
    bool ShowToolOutput = false,
    IReadOnlyDictionary<string, ExtensionChatThemeToken>? ThemeTokens = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? CustomType = null,
    object? CustomContent = null,
    object? CustomDetails = null,
    bool CustomDisplay = true);

public delegate IReadOnlyList<ExtensionChatRow> ExtensionMessageRenderHandler(ExtensionChatRowRenderContext context);
public delegate IReadOnlyList<ExtensionChatRow> ExtensionMessageDecorateHandler(ExtensionChatRowRenderContext context, IReadOnlyList<ExtensionChatRow> rows);
