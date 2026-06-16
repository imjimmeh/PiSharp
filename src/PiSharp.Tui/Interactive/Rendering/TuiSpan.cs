namespace PiSharp.Tui.Interactive.Rendering;

public enum TuiSpanKind
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

public sealed record TuiSpan(string Text, TuiSpanKind Kind = TuiSpanKind.Text);

public sealed record TuiRenderLine(IReadOnlyList<TuiSpan> Spans)
{
    public TuiRenderLine(params TuiSpan[] spans) : this((IReadOnlyList<TuiSpan>)spans) { }

    public string Text => string.Concat(Spans.Select(span => span.Text));

    public static TuiRenderLine FromText(string text, TuiSpanKind kind = TuiSpanKind.Text)
        => new(new TuiSpan(text, kind));
}
