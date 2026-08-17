using System.Text;
using PiSharp.Tui.Interactive.Theme;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Rendering;

public sealed record AnsiStyledRun(string Text, Terminal.Gui.Attribute Attribute, int Row = 0, int Column = 0, TuiSpanKind SpanKind = TuiSpanKind.Text);

public sealed record AnsiStyledText(IReadOnlyList<AnsiStyledRun> Runs)
{
    public static Terminal.Gui.Attribute BrightTextAttribute { get; } = new(new Color("#e8e0d5"), new Color("#16130f"));

    public string Text => string.Concat(Runs.Select(run => run.Text));

    public static AnsiStyledText Parse(string? text, Terminal.Gui.Attribute? defaultAttribute = null, TuiSpanKind defaultSpanKind = TuiSpanKind.Text)
    {
        var fallback = defaultAttribute ?? TuiTheme.GetTokenAttribute(TuiThemeToken.Text);
        var current = fallback;
        var currentKind = defaultSpanKind;
        var runs = new List<AnsiStyledRun>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            runs.Add(new AnsiStyledRun(buffer.ToString(), current, SpanKind: currentKind));
            buffer.Clear();
        }

        var source = text ?? string.Empty;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '')
            {
                Flush();
                if (index + 1 >= source.Length) continue;

                switch (source[index + 1])
                {
                    case '[':
                        TryConsumeCsi(source, index, fallback, current, currentKind, defaultSpanKind, out current, out currentKind, out index);
                        continue;
                    case ']':
                    case 'P':
                    case '_':
                    case '^':
                        index = ConsumeStringSequence(source, index + 2);
                        continue;
                    default:
                        index++;
                        continue;
                }
            }

            if (char.IsControl(source[index]) && source[index] is not '\n' and not '\t') continue;

            buffer.Append(source[index]);
        }

        Flush();
        return new AnsiStyledText(runs.Count == 0 ? [new AnsiStyledRun(string.Empty, fallback, SpanKind: defaultSpanKind)] : runs);
    }

    public TuiRenderLine ToRenderLine()
        => new(Runs.Select(run => new TuiSpan(run.Text, run.SpanKind)).ToArray());

    public IReadOnlyList<AnsiStyledText> Wrap(int width)
        => TuiTextWrapping.WrapRanges(Text, width)
            .Select(range => Slice(range.Offset, range.Length))
            .ToArray();

    private AnsiStyledText Slice(int offset, int length)
    {
        if (length == 0)
        {
            var fallback = Runs.FirstOrDefault();
            return new AnsiStyledText([new AnsiStyledRun(string.Empty, fallback?.Attribute ?? TuiTheme.GetTokenAttribute(TuiThemeToken.Text), SpanKind: fallback?.SpanKind ?? TuiSpanKind.Text)]);
        }

        var runs = new List<AnsiStyledRun>();
        var runStart = 0;
        foreach (var run in Runs)
        {
            var runEnd = runStart + run.Text.Length;
            var takeStart = Math.Max(offset, runStart);
            var takeEnd = Math.Min(offset + length, runEnd);
            if (takeEnd > takeStart)
            {
                runs.Add(run with { Text = run.Text[(takeStart - runStart)..(takeEnd - runStart)] });
            }

            runStart = runEnd;
        }

        return new AnsiStyledText(runs);
    }

    private static bool TryConsumeCsi(
        string source,
        int escapeIndex,
        Terminal.Gui.Attribute fallback,
        Terminal.Gui.Attribute current,
        TuiSpanKind currentKind,
        TuiSpanKind defaultSpanKind,
        out Terminal.Gui.Attribute nextAttribute,
        out TuiSpanKind nextKind,
        out int nextIndex)
    {
        var parameterStart = escapeIndex + 2;
        var index = parameterStart;
        while (index < source.Length && IsCsiParameterByte(source[index])) index++;

        while (index < source.Length && IsCsiIntermediateByte(source[index])) index++;

        if (index >= source.Length)
        {
            nextAttribute = current;
            nextKind = currentKind;
            nextIndex = source.Length - 1;
            return true;
        }

        if (source[index] == 'm')
        {
            (nextAttribute, nextKind) = ApplyCodes(source[parameterStart..index], fallback, current, currentKind, defaultSpanKind);
            nextIndex = index;
            return true;
        }

        nextAttribute = current;
        nextKind = currentKind;
        nextIndex = IsCsiFinalByte(source[index]) ? index : Math.Max(escapeIndex, index - 1);
        return true;
    }

    private static int ConsumeStringSequence(string source, int offset)
    {
        var index = offset;
        while (index < source.Length)
        {
            if (source[index] == '\u0007') return index;
            if (source[index] == '\u001b' && index + 1 < source.Length && source[index + 1] == '\\') return index + 1;
            index++;
        }

        return source.Length - 1;
    }

    private static bool IsCsiParameterByte(char value)
        => value is >= '0' and <= '9'
            or ';'
            or ':'
            or '?'
            or '<'
            or '='
            or '>';

    private static bool IsCsiIntermediateByte(char value)
        => value is >= ' ' and <= '/';

    private static bool IsCsiFinalByte(char value)
        => value is >= '@' and <= '~';

    private static (Terminal.Gui.Attribute Attribute, TuiSpanKind SpanKind) ApplyCodes(
        string codesText,
        Terminal.Gui.Attribute fallback,
        Terminal.Gui.Attribute current,
        TuiSpanKind currentKind,
        TuiSpanKind defaultSpanKind)
    {
        var codes = string.IsNullOrWhiteSpace(codesText)
            ? ["0"]
            : codesText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var next = current;
        var nextKind = currentKind;
        foreach (var code in codes)
        {
            (next, nextKind) = code switch
            {
                "0" or "39" or "22" or "29" => (fallback, defaultSpanKind),
                "31" or "91" => (TuiTheme.GetTokenAttribute(TuiThemeToken.Error), TuiSpanKind.Error),
                "32" or "92" => (TuiTheme.GetTokenAttribute(TuiThemeToken.Success), TuiSpanKind.Success),
                "33" or "93" => (TuiTheme.GetTokenAttribute(TuiThemeToken.Warning), TuiSpanKind.Warning),
                "36" or "96" => (TuiTheme.GetTokenAttribute(TuiThemeToken.Accent), TuiSpanKind.Accent),
                "37" => (TuiTheme.GetTokenAttribute(TuiThemeToken.Text), TuiSpanKind.Text),
                "97" => (BrightTextAttribute, TuiSpanKind.Text),
                "90" => (TuiTheme.GetTokenAttribute(TuiThemeToken.Dim), TuiSpanKind.Muted),
                _ => (next, nextKind)
            };
        }

        return (next, nextKind);
    }
}
