using PiSharp.Extensions;

namespace PiSharp.Tui.Interactive.Rendering;

internal static class ExtensionChatRowMapper
{
    public static TuiChatRow ToTuiRow(ExtensionChatRow row)
        => new(
            row.Text ?? string.Empty,
            ToTuiKind(row.Kind),
            ToTuiTarget(row.InteractionTarget),
            row.Spans?.Select(ToTuiSpan).ToArray(),
            ToTuiTarget(row.ContextTarget));

    public static ExtensionChatRow ToExtensionRow(TuiChatRow row)
        => new(
            row.Text,
            ToExtensionKind(row.Kind),
            row.Spans?.Select(ToExtensionSpan).ToArray(),
            ToExtensionTarget(row.InteractionTarget),
            ToExtensionTarget(row.ContextTarget));

    public static TuiInteractionTarget? ToTuiTarget(ExtensionChatInteractionTarget? target)
        => target is null ? null : new TuiInteractionTarget(target.Kind, target.Id, target.SourceId, target.Action, target.Data);

    public static ExtensionChatInteractionTarget? ToExtensionTarget(TuiInteractionTarget? target)
        => target is null ? null : new ExtensionChatInteractionTarget(target.Kind, target.Id, target.SourceId, target.Action, target.Data);

    private static TuiSpan ToTuiSpan(ExtensionChatSpan span)
        => new(span.Text ?? string.Empty, ToTuiSpanKind(span.Kind));

    private static ExtensionChatSpan ToExtensionSpan(TuiSpan span)
        => new(span.Text, ToExtensionSpanKind(span.Kind));

    private static TuiChatRowKind ToTuiKind(ExtensionChatRowKind kind)
        => kind switch
        {
            ExtensionChatRowKind.User => TuiChatRowKind.User,
            ExtensionChatRowKind.Assistant => TuiChatRowKind.Assistant,
            ExtensionChatRowKind.AssistantThinking => TuiChatRowKind.AssistantThinking,
            ExtensionChatRowKind.Custom => TuiChatRowKind.Custom,
            ExtensionChatRowKind.ToolRunning => TuiChatRowKind.ToolRunning,
            ExtensionChatRowKind.ToolSucceeded => TuiChatRowKind.ToolSucceeded,
            ExtensionChatRowKind.ToolFailed => TuiChatRowKind.ToolFailed,
            ExtensionChatRowKind.System => TuiChatRowKind.System,
            ExtensionChatRowKind.Error => TuiChatRowKind.Error,
            _ => TuiChatRowKind.Normal
        };

    private static ExtensionChatRowKind ToExtensionKind(TuiChatRowKind kind)
        => kind switch
        {
            TuiChatRowKind.User => ExtensionChatRowKind.User,
            TuiChatRowKind.Assistant => ExtensionChatRowKind.Assistant,
            TuiChatRowKind.AssistantThinking => ExtensionChatRowKind.AssistantThinking,
            TuiChatRowKind.Custom => ExtensionChatRowKind.Custom,
            TuiChatRowKind.ToolRunning => ExtensionChatRowKind.ToolRunning,
            TuiChatRowKind.ToolSucceeded => ExtensionChatRowKind.ToolSucceeded,
            TuiChatRowKind.ToolFailed => ExtensionChatRowKind.ToolFailed,
            TuiChatRowKind.System => ExtensionChatRowKind.System,
            TuiChatRowKind.Error => ExtensionChatRowKind.Error,
            _ => ExtensionChatRowKind.Normal
        };

    private static TuiSpanKind ToTuiSpanKind(ExtensionChatSpanKind kind)
        => kind switch
        {
            ExtensionChatSpanKind.Muted => TuiSpanKind.Muted,
            ExtensionChatSpanKind.Accent => TuiSpanKind.Accent,
            ExtensionChatSpanKind.Success => TuiSpanKind.Success,
            ExtensionChatSpanKind.Warning => TuiSpanKind.Warning,
            ExtensionChatSpanKind.Error => TuiSpanKind.Error,
            ExtensionChatSpanKind.Code => TuiSpanKind.Code,
            ExtensionChatSpanKind.Border => TuiSpanKind.Border,
            ExtensionChatSpanKind.Heading => TuiSpanKind.Heading,
            ExtensionChatSpanKind.Link => TuiSpanKind.Link,
            _ => TuiSpanKind.Text
        };

    private static ExtensionChatSpanKind ToExtensionSpanKind(TuiSpanKind kind)
        => kind switch
        {
            TuiSpanKind.Muted => ExtensionChatSpanKind.Muted,
            TuiSpanKind.Accent => ExtensionChatSpanKind.Accent,
            TuiSpanKind.Success => ExtensionChatSpanKind.Success,
            TuiSpanKind.Warning => ExtensionChatSpanKind.Warning,
            TuiSpanKind.Error => ExtensionChatSpanKind.Error,
            TuiSpanKind.Code => ExtensionChatSpanKind.Code,
            TuiSpanKind.Border => ExtensionChatSpanKind.Border,
            TuiSpanKind.Heading => ExtensionChatSpanKind.Heading,
            TuiSpanKind.Link => ExtensionChatSpanKind.Link,
            _ => ExtensionChatSpanKind.Text
        };
}
