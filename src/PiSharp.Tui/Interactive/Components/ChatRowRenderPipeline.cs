using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

public sealed class ChatRowRenderPipeline(ExtensionRegistry? registry = null, ILoggerFactory? loggerFactory = null)
{
    private readonly ExtensionRegistry? _registry = registry;
    private readonly ILogger<ChatRowRenderPipeline> _logger = loggerFactory?.CreateLogger<ChatRowRenderPipeline>() ?? NullLogger<ChatRowRenderPipeline>.Instance;

    public IReadOnlyList<TuiChatRow> Render(TuiTranscriptItem item, TuiRenderState state, int width = 100)
    {
        width = Math.Max(1, width);
        var context = CreateContext(item, state, width);
        var renderer = ResolveRenderer(context.RowType, context.CustomType);
        IReadOnlyList<TuiChatRow> rows;
        try
        {
            rows = renderer is not null
                ? renderer.Value.Handler!(context).Select(ExtensionChatRowMapper.ToTuiRow).ToArray()
                : TuiMessageRenderer.Render(item, state, width);
            if (renderer is not null && rows.Count == 0)
                rows = TuiMessageRenderer.Render(item, state, width);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extension renderer for {RowType} failed, falling back to built-in", context.RowType);
            rows = TuiMessageRenderer.Render(item, state, width);
        }

        foreach (var decorator in ResolveDecorators(context.RowType, context.CustomType))
        {
            try
            {
                var extensionRows = rows.Select(ExtensionChatRowMapper.ToExtensionRow).ToArray();
                rows = decorator.Value.Handler!(context, extensionRows).Select(ExtensionChatRowMapper.ToTuiRow).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extension decorator {SourceId}/{Name} failed", decorator.SourceId, decorator.Value.Name);
            }
        }

        return ApplyFinalSafety(rows, item, context.RowType, width).ToArray();
    }

    private OwnedExtensionRegistration<ExtensionMessageRendererRegistration>? ResolveRenderer(ExtensionChatRowType rowType, string? customType = null)
    {
        if (_registry is null) return null;
        var byCustomType = !string.IsNullOrWhiteSpace(customType)
            ? _registry.FindRendererByCustomType(customType)
            : null;
        if (byCustomType is not null && byCustomType.Value.Handler is not null)
            return byCustomType;
        return _registry.Renderers.LastOrDefault(registration => registration.Value.RowType == rowType
            && registration.Value.Handler is not null
            && string.IsNullOrWhiteSpace(registration.Value.CustomType)
            && registration.Value.Override == ExtensionOverridePolicy.OverrideBuiltIn);
    }

    private IReadOnlyList<OwnedExtensionRegistration<ExtensionMessageDecoratorRegistration>> ResolveDecorators(ExtensionChatRowType rowType, string? customType)
        => (_registry?.Decorators ?? [])
            .Where(registration => registration.Value.Handler is not null
                && (!string.IsNullOrWhiteSpace(registration.Value.CustomType)
                    ? !string.IsNullOrWhiteSpace(customType) && string.Equals(registration.Value.CustomType, customType, StringComparison.Ordinal)
                    : registration.Value.RowType == rowType))
            .OrderBy(registration => registration.Value.Order)
            .ThenBy(registration => registration.SourceId, StringComparer.Ordinal)
            .ThenBy(registration => registration.Value.Name, StringComparer.Ordinal)
            .ToArray();

    private static ExtensionChatRowRenderContext CreateContext(TuiTranscriptItem item, TuiRenderState state, int width)
    {
        var metadata = Metadata(item);
        var customType = metadata.GetValueOrDefault("customType");
        return new(
            RowType(item),
            item.Role,
            item.Text,
            item.Content,
            item.ToolName,
            item.ToolCallId,
            item.ToolArguments,
            item.ToolResult,
            item.EntryId,
            item.IsStreaming,
            item.IsError,
            item.IsExpanded,
            width,
            state.ShowThinking,
            state.ShowToolOutput,
            Metadata: metadata,
            CustomType: string.IsNullOrWhiteSpace(customType) ? null : customType,
            CustomContent: item.CustomContent,
            CustomDetails: item.CustomDetails,
            CustomDisplay: item.CustomDisplay);
    }

    public static ExtensionChatRowType RowType(TuiTranscriptItem item)
        => item.Role switch
        {
            "user" => ExtensionChatRowType.User,
            "assistant" when item.Content?.Any(content => content is PiSharp.Abstractions.Messages.ThinkingContent) == true
                && item.Content.All(content => content is PiSharp.Abstractions.Messages.ThinkingContent) => ExtensionChatRowType.AssistantThinking,
            "assistant" => ExtensionChatRowType.Assistant,
            "tool" => ExtensionChatRowType.ToolCall,
            "toolResult" => ExtensionChatRowType.ToolResult,
            "system" when item.IsError => ExtensionChatRowType.Error,
            "system" => ExtensionChatRowType.System,
            "custom" => ExtensionChatRowType.Custom,
            _ => ExtensionChatRowType.Custom
        };

    private static IReadOnlyDictionary<string, string> Metadata(TuiTranscriptItem item)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["role"] = item.Role
        };
        if (!string.IsNullOrWhiteSpace(item.ToolName)) metadata["toolName"] = item.ToolName!;
        if (!string.IsNullOrWhiteSpace(item.ToolCallId)) metadata["toolCallId"] = item.ToolCallId!;
        if (!string.IsNullOrWhiteSpace(item.EntryId)) metadata["entryId"] = item.EntryId!;
        if (!string.IsNullOrWhiteSpace(item.CustomType)) metadata["customType"] = item.CustomType!;
        return metadata;
    }

    private static IEnumerable<TuiChatRow> ApplyFinalSafety(IReadOnlyList<TuiChatRow> rows, TuiTranscriptItem item, ExtensionChatRowType rowType, int width)
    {
        var toolTarget = rowType == ExtensionChatRowType.ToolCall && !string.IsNullOrWhiteSpace(item.ToolCallId)
            ? new TuiInteractionTarget("tool", item.ToolCallId!, Action: "toggle")
            : null;

        foreach (var row in rows)
        {
            var safe = StripUnsafeControls(row);
            if (toolTarget is not null && safe.InteractionTarget is null) safe = safe with { InteractionTarget = toolTarget };
            yield return safe.PadTo(width);
        }
    }

    private static TuiChatRow StripUnsafeControls(TuiChatRow row)
    {
        var text = StripUnsafeControls(row.Text);
        var spans = row.Spans?.Select(span => span with { Text = StripUnsafeControls(span.Text) }).ToArray();
        return row with { Text = text, Spans = spans };
    }

    private static string StripUnsafeControls(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var withoutAnsi = Regex.Replace(text, @"\u001B\[[0-?]*[ -/]*[@-~]", string.Empty);
        return new string(withoutAnsi.Where(ch => ch == '\n' || ch == '\t' || !char.IsControl(ch)).ToArray());
    }
}
