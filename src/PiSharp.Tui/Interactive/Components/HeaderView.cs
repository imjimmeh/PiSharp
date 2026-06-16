namespace PiSharp.Tui.Interactive.Components;

public sealed class HeaderView : WrappedTextView
{
    public HeaderView() : base(fallbackWidth: 100, initialHeight: 3)
    {
    }

    public void Render(TuiRenderState state, bool expanded, TuiExtensionLoadStatus? extensionLoadStatus = null)
    {
        var title = string.IsNullOrWhiteSpace(state.TitleOverride) ? "PiSharp" : state.TitleOverride;
        var session = string.IsNullOrWhiteSpace(state.SessionName) ? state.SessionId : state.SessionName;
        var headerHints = TuiKeybindings.HeaderHints.OrderBy(hint => hint.Order).ToArray();
        var headerHintText = string.Join(" · ", headerHints.Select(hint => $"{hint.Keys} {hint.Label}"));
        var detailsHint = headerHints.LastOrDefault(hint => string.Equals(hint.Label, "details", StringComparison.Ordinal));
        var detailsHintKey = detailsHint?.Keys ?? "Ctrl+H";
        var detailsHintLabel = detailsHint?.Label ?? "details";

        var extensionIndicator = extensionLoadStatus is null || extensionLoadStatus.Total == 0
            ? string.Empty
            : extensionLoadStatus.IsLoading
                ? $" · ext loading {extensionLoadStatus.Ready + extensionLoadStatus.Failed}/{extensionLoadStatus.Total}"
                : extensionLoadStatus.Failed > 0
                    ? $" · ext ready {extensionLoadStatus.Ready}/{extensionLoadStatus.Total} failed {extensionLoadStatus.Failed}"
                    : $" · ext ready {extensionLoadStatus.Ready}/{extensionLoadStatus.Total}";

        var lines = new List<string>
        {
            $"╔═ {title} [{state.Status}{extensionIndicator}]",
            $"║ {headerHintText}"
        };

        var detail = expanded
            ? $"session:{session} · model:{state.ModelDisplay} · thinking:{state.ThinkingLevel.ToString().ToLowerInvariant()} · /help"
            : $"session:{session} · {detailsHintKey} for {detailsHintLabel}";
        lines.Add($"╚═ {detail}");

        RenderWrapped(lines, () => Render(state, expanded));
    }
}
