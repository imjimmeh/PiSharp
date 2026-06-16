using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;

namespace PiSharp.Runtime.Session;

public static partial class HtmlSessionRenderer
{
    [GeneratedRegex(@"[^a-z0-9-]", RegexOptions.IgnoreCase)]
    private static partial Regex NonCssClassCharPattern();

    public static async Task<string> RenderAsync(ISession<JsonlSessionMetadata> session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var entries = await session.GetEntriesAsync(cancellationToken);
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>PiSharp Session Export</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 900px; margin: 0 auto; padding: 2rem; background: #1e1e2e; color: #cdd6f4; }");
        sb.AppendLine("  h1 { color: #cba6f7; border-bottom: 1px solid #45475a; padding-bottom: 0.5rem; }");
        sb.AppendLine("  .entry { margin: 1rem 0; padding: 1rem; border-radius: 8px; border-left: 4px solid; }");
        sb.AppendLine("  .entry-header { font-size: 0.85rem; color: #a6adc8; margin-bottom: 0.5rem; }");
        sb.AppendLine("  .entry-content { white-space: pre-wrap; word-break: break-word; }");
        sb.AppendLine("  .role-user { background: #313244; border-color: #89b4fa; }");
        sb.AppendLine("  .role-assistant { background: #313244; border-color: #a6e3a1; }");
        sb.AppendLine("  .role-toolResult { background: #313244; border-color: #f9e2af; }");
        sb.AppendLine("  .role-system { background: #313244; border-color: #f38ba8; }");
        sb.AppendLine("  .type-thinking_level_change, .type-model_change, .type-compaction, .type-branch_summary, .type-custom, .type-custom_message, .type-label, .type-session_info, .type-leaf { background: #313244; border-color: #6c7086; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<h1>PiSharp Session Export</h1>");

        foreach (var entry in entries)
        {
            RenderEntry(sb, entry);
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    public static async Task ExportToFileAsync(ISession<JsonlSessionMetadata> session, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var html = await RenderAsync(session, cancellationToken);
        await File.WriteAllTextAsync(outputPath, html, cancellationToken);
    }

    private static void RenderEntry(StringBuilder sb, SessionTreeEntry entry)
    {
        var cssClass = GetCssClass(entry);

        sb.Append("<div class=\"entry ");
        sb.Append(cssClass);
        sb.AppendLine("\">");

        sb.Append("<div class=\"entry-header\">");
        sb.Append(System.Net.WebUtility.HtmlEncode(entry.Type));
        if (entry is MessageEntry m)
        {
            sb.Append(" &mdash; ");
            sb.Append(System.Net.WebUtility.HtmlEncode(m.Message.Role));
        }
        sb.Append(" &mdash; ");
        sb.Append(System.Net.WebUtility.HtmlEncode(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine("</div>");

        sb.Append("<div class=\"entry-content\">");
        sb.Append(System.Net.WebUtility.HtmlEncode(FormatEntryContent(entry)));
        sb.AppendLine("</div>");

        sb.AppendLine("</div>");
    }

    private static string GetCssClass(SessionTreeEntry entry)
    {
        if (entry is MessageEntry m)
        {
            return "role-" + SanitizeCssClassPart(m.Message.Role);
        }

        return "type-" + SanitizeCssClassPart(entry.Type);
    }

    private static string SanitizeCssClassPart(string value)
        => NonCssClassCharPattern().Replace(value, "-");

    private static IReadOnlyList<MessageContent> GetMessageContent(AgentMessage message)
    {
        switch (message)
        {
            case UserMessage u: return u.Content;
            case AssistantMessage a: return a.Content;
            case ToolResultMessage t: return t.Content;
            default: return [];
        }
    }

    private static string GetMessageText(AgentMessage message)
    {
        var texts = GetMessageContent(message).OfType<TextContent>().Select(c => c.Text).ToArray();
        return texts.Length > 0 ? string.Join("\n", texts) : message.ToString() ?? string.Empty;
    }

    private static string FormatEntryContent(SessionTreeEntry entry)
    {
        switch (entry)
        {
            case MessageEntry m:
                return GetMessageText(m.Message);

            case ThinkingLevelChangeEntry t:
                return $"Thinking level changed to: {t.ThinkingLevel}";

            case ModelChangeEntry mc:
                return $"Model changed to: {mc.Provider}/{mc.ModelId}";

            case CompactionEntry c:
                return $"Compaction: {c.Summary} (tokens before: {c.TokensBefore})";

            case BranchSummaryEntry b:
                return $"Branch summary: {b.Summary}";

            case CustomEntry ce:
                return $"Custom [{ce.CustomType}]: {ce.Data}";

            case CustomMessageEntry cm:
                return $"Custom message [{cm.CustomType}]: {cm.Content}";

            case LabelEntry l:
                return $"Label: {l.Label} (target: {l.TargetId})";

            case SessionInfoEntry si:
                return $"Session name: {si.Name}";

            case LeafEntry le:
                return $"Leaf: {le.TargetId}";

            default:
                return entry.ToString() ?? string.Empty;
        }
    }
}
