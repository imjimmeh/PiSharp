namespace PiSharp.Tui.Interactive.Rendering;

public enum TuiChatRowKind
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

public sealed record TuiInteractionTarget(
    string Kind,
    string Id,
    string? SourceId = null,
    string? Action = null,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record TuiInteractionHit(
    TuiInteractionTarget Target,
    int SourceRow,
    int ViewRow,
    int Column);

public sealed record TuiChatRow(
    string Text,
    TuiChatRowKind Kind = TuiChatRowKind.Normal,
    TuiInteractionTarget? InteractionTarget = null,
    IReadOnlyList<TuiSpan>? Spans = null,
    TuiInteractionTarget? ContextTarget = null)
{
    public TuiChatRow PadTo(int width)
    {
        if (width <= 0) return this;
        var text = Text.Length > width ? TuiRenderBuffer.Truncate(Text, width) : Text.PadRight(width);
        return this with { Text = text, Spans = PadSpans(text) };
    }

    private IReadOnlyList<TuiSpan>? PadSpans(string paddedText)
    {
        if (Spans is null || Spans.Count == 0) return Spans;
        var spanTextLength = Spans.Sum(span => span.Text.Length);
        if (spanTextLength == paddedText.Length) return Spans;
        if (spanTextLength < paddedText.Length)
        {
            return Spans.Concat([new TuiSpan(paddedText[spanTextLength..])]).ToArray();
        }

        return TruncateSpans(Spans, paddedText);
    }

    private static IReadOnlyList<TuiSpan> TruncateSpans(IReadOnlyList<TuiSpan> spans, string truncatedText)
    {
        var offset = 0;
        var output = new List<TuiSpan>();
        foreach (var span in spans)
        {
            if (offset >= truncatedText.Length) break;
            var take = Math.Min(truncatedText.Length - offset, span.Text.Length);
            output.Add(span with { Text = truncatedText.Substring(offset, take) });
            offset += take;
        }

        if (offset < truncatedText.Length)
        {
            if (output.Count == 0) output.Add(new TuiSpan(truncatedText));
            else output[^1] = output[^1] with { Text = output[^1].Text + truncatedText[offset..] };
        }

        return output;
    }
}
