using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

public sealed class FooterView : WrappedTextView
{
    private readonly ILogger<FooterView> _logger;

    public FooterView(ILoggerFactory? loggerFactory = null) : base(fallbackWidth: 120, initialHeight: 2)
    {
        _logger = loggerFactory?.CreateLogger<FooterView>() ?? NullLogger<FooterView>.Instance;
    }

    public void Render(TuiRenderState state, TuiFooterSnapshot? snapshot = null, IReadOnlyList<string>? activeTools = null, int? widthOverride = null)
    {
        snapshot ??= FooterDataProvider.CreateSnapshot(state, Environment.CurrentDirectory);
        var width = widthOverride is > 0 ? widthOverride.Value : TuiViewSizing.ResolveWidth(this, 120);
        var customFooterLines = CustomFooterLines(state).ToArray();
        if (customFooterLines.Length > 0)
        {
            _logger.LogDebug("Footer rendered custom lines width={Width} customFooterLines={CustomFooterLineCount}", width, customFooterLines.Length);
            RenderWrapped(customFooterLines, () => Render(state, snapshot, activeTools, widthOverride), widthOverride);
            return;
        }

        var tools = activeTools is { Count: > 0 } ? string.Join(",", activeTools) : "none";
        var branch = string.IsNullOrWhiteSpace(snapshot.GitBranch) ? string.Empty : $" ({snapshot.GitBranch})";
        var statusSgr = state.IsBusy
            ? "\u001b[96m"   // Accent
            : state.Status?.StartsWith("error", StringComparison.OrdinalIgnoreCase) == true
                ? "\u001b[31m" // Error
                : "\u001b[32m"; // Success
        var statusText = $"{statusSgr}{state.Status}\u001b[39m";
        var firstLine = $"{snapshot.Cwd}{branch} • {statusText} • tools:{tools}";

        string contextPart;
        if (!snapshot.ContextPercentKnown)
        {
            contextPart = $"ctx ?/{FooterDataProvider.FormatTokenCount(snapshot.ContextWindow)}";
        }
        else if (snapshot.ContextPercent > 90)
        {
            contextPart = $"ctx! {snapshot.ContextPercent:0.0}%/{FooterDataProvider.FormatTokenCount(snapshot.ContextWindow)}";
        }
        else if (snapshot.ContextPercent > 70)
        {
            contextPart = $"ctx~ {snapshot.ContextPercent:0.0}%/{FooterDataProvider.FormatTokenCount(snapshot.ContextWindow)}";
        }
        else
        {
            contextPart = $"ctx {snapshot.ContextPercent:0.0}%/{FooterDataProvider.FormatTokenCount(snapshot.ContextWindow)}";
        }

        var leftParts = new List<string>();
        if (snapshot.InputTokens > 0) leftParts.Add($"↑{FooterDataProvider.FormatTokenCount(snapshot.InputTokens)}");
        if (snapshot.OutputTokens > 0) leftParts.Add($"↓{FooterDataProvider.FormatTokenCount(snapshot.OutputTokens)}");
        if (snapshot.CacheTokens > 0) leftParts.Add($"R/W{FooterDataProvider.FormatTokenCount(snapshot.CacheTokens)}");
        if (snapshot.TotalCost > 0) leftParts.Add($"${snapshot.TotalCost:0.000}");
        leftParts.Add(contextPart + (snapshot.AutoCompact ? " (auto)" : string.Empty));
        var statsLeft = string.Join(' ', leftParts);

        var rightSide = $"{state.ModelDisplay} • thinking {state.ThinkingLevel.ToString().ToLowerInvariant()}";
        var lines = new List<string> { firstLine };
        lines.AddRange(FormatStatsLines(statsLeft, rightSide, width));

        if (snapshot.ExtensionStatuses.Count > 0)
        {
            lines.Add(string.Join(' ', snapshot.ExtensionStatuses.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => StyleExtensionText(SanitizeStatus(pair.Value)))));
        }

        _logger.LogDebug(
            "Footer rendered model={ModelDisplay} thinking={ThinkingLevel} width={Width} branch={GitBranch} customFooterLines={CustomFooterLineCount} extensionStatusCount={ExtensionStatusCount}",
            state.ModelDisplay,
            state.ThinkingLevel,
            width,
            snapshot.GitBranch,
            customFooterLines.Length,
            snapshot.ExtensionStatuses.Count);
        RenderWrapped(lines, () => Render(state, snapshot, activeTools, widthOverride), widthOverride);
    }

    private static IEnumerable<string> CustomFooterLines(TuiRenderState state)
        => state.BridgeSlots
            .Where(slot => slot.Visible && string.Equals(slot.Placement, "footer", StringComparison.Ordinal))
            .OrderBy(slot => slot.Id, StringComparer.Ordinal)
            .SelectMany(slot => SplitLines(slot.Content).Select(StyleExtensionText));

    private static IEnumerable<string> SplitLines(string content)
        => (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => line.Length > 0);

    private static string SanitizeStatus(string text)
        => (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    private static string StyleExtensionText(string text)
        => string.IsNullOrEmpty(text) || text.Contains('\u001b', StringComparison.Ordinal)
            ? text
            : $"\u001b[97m{text}\u001b[39m";

    private static IReadOnlyList<string> FormatStatsLines(string left, string right, int width)
    {
        left = left ?? string.Empty;
        right = right ?? string.Empty;

        const int minimumPadding = 2;
        if (left.Length + minimumPadding + right.Length <= width)
        {
            return [left + new string(' ', width - left.Length - right.Length) + right];
        }

        return [$"{left} {right}".Trim()];
    }
}
