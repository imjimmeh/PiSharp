using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Rendering;
using PiSharp.Tui.Interactive.Theme;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed record ExtensionCustomUiSnapshot(string RequestId, IReadOnlyList<string> Lines, int Width, int Height, bool Completed = false, object? Value = null, string? Error = null);

public static class ExtensionCustomUiSnapshotRenderer
{
    public static IReadOnlyList<string> RenderLines(ExtensionCustomUiSnapshot snapshot, int width)
        => snapshot.Lines.Select(line => AnsiStyledText.Parse(line ?? string.Empty).Text).ToArray();
}

public static class ExtensionCustomUiInputTranslator
{
    public static string TranslateKeyNameForTest(string keyName)
        => keyName switch
        {
            nameof(KeyCode.CursorDown) => "\u001b[B",
            nameof(KeyCode.CursorUp) => "\u001b[A",
            nameof(KeyCode.CursorLeft) => "\u001b[D",
            nameof(KeyCode.CursorRight) => "\u001b[C",
            nameof(KeyCode.Enter) => "\r",
            nameof(KeyCode.Esc) => "\u001b",
            "Escape" => "\u001b",
            nameof(KeyCode.Space) => " ",
            nameof(KeyCode.Tab) => "\t",
            nameof(KeyCode.Backspace) => "\u007f",
            nameof(KeyCode.Delete) => "\u001b[3~",
            nameof(KeyCode.Home) => "\u001b[H",
            nameof(KeyCode.End) => "\u001b[F",
            nameof(KeyCode.PageUp) => "\u001b[5~",
            nameof(KeyCode.PageDown) => "\u001b[6~",
            _ => throw new ArgumentOutOfRangeException(nameof(keyName), keyName, "Unsupported key name.")
        };

    public static string TranslateMouseClickForTest(int x, int y)
        => $"\u001b[<0;{x + 1};{y + 1}M";

    public static bool TryTranslate(Key key, out string data)
    {
        if (TryTranslateSpecialKey(key, out data)) return true;
        return TuiKeyText.TryGetPrintableText(key, out data);
    }

    private static bool TryTranslateSpecialKey(Key key, out string data)
    {
        if (key.IsCtrl && !key.IsAlt && TryTranslateCtrlKey(key, out data))
            return true;

        var baseKeyCode = key.KeyCode & ~(KeyCode.ShiftMask | KeyCode.CtrlMask | KeyCode.AltMask);
        if (key.IsShift && !key.IsCtrl && !key.IsAlt && baseKeyCode == KeyCode.Tab)
        {
            data = "\u001b[Z";
            return true;
        }

        data = baseKeyCode switch
        {
            KeyCode.CursorDown => "\u001b[B",
            KeyCode.CursorUp => "\u001b[A",
            KeyCode.CursorLeft => "\u001b[D",
            KeyCode.CursorRight => "\u001b[C",
            KeyCode.Enter => "\r",
            KeyCode.Esc => "\u001b",
            KeyCode.Space => " ",
            KeyCode.Tab => "\t",
            KeyCode.Backspace => "\u007f",
            KeyCode.Delete => "\u001b[3~",
            KeyCode.Home => "\u001b[H",
            KeyCode.End => "\u001b[F",
            KeyCode.PageUp => "\u001b[5~",
            KeyCode.PageDown => "\u001b[6~",
            _ => string.Empty
        };

        return data.Length > 0;
    }

    private static bool TryTranslateCtrlKey(Key key, out string data)
    {
        var keyCode = key.KeyCode & KeyCode.CharMask;
        data = keyCode switch
        {
            KeyCode.J => "\n",
            KeyCode.M => "\r",
            (KeyCode)']' => "\u001d",
            _ => string.Empty
        };

        if (data.Length > 0) return true;

        var ascii = (int)keyCode;
        if (ascii is >= 'A' and <= 'Z')
        {
            data = char.ConvertFromUtf32(ascii - '@');
            return true;
        }

        if (ascii is >= 'a' and <= 'z')
        {
            data = char.ConvertFromUtf32(ascii - '`');
            return true;
        }

        return false;
    }
}

public sealed class ExtensionCustomUiOverlay : Window
{
    private ExtensionCustomUiSnapshot? _snapshot;
    private IReadOnlyList<string> _renderedLines = [];
    private IReadOnlyList<AnsiStyledRun> _styledRuns = [];
    private int _lastForwardedWidth;
    private int _lastForwardedHeight;

    public ExtensionCustomUiOverlay()
    {
        Title = "Extension UI";
        CanFocus = true;
        BorderStyle = LineStyle.Single;
        ColorScheme = TuiTheme.PopupColorScheme;
        FrameChanged += (_, _) => HandleFrameChanged();
    }

    public string? RequestId => _snapshot?.RequestId;
    internal ExtensionCustomUiSnapshot? Snapshot => _snapshot;
    public string? SourceId { get; internal set; }
    public IReadOnlyList<string> RenderedLines => _renderedLines;
    public IReadOnlyList<AnsiStyledRun> StyledRuns => _styledRuns;
    public Action<string>? ForwardInput { get; set; }
    public Action<int, int>? ForwardResize { get; set; }

    public void UpdateSnapshot(ExtensionCustomUiSnapshot snapshot)
    {
        _snapshot = snapshot;
        var parsedLines = snapshot.Lines.Select(line => AnsiStyledText.Parse(line ?? string.Empty)).ToArray();
        _renderedLines = parsedLines.Select(line => line.Text).ToArray();
        _styledRuns = BuildRuns(parsedLines);
        Text = string.Join('\n', _renderedLines);
        _lastForwardedWidth = Math.Max(1, Frame.Width);
        _lastForwardedHeight = Math.Max(1, Frame.Height);
        SetNeedsDraw();
    }

    public bool HandleKeyDown(Key key)
    {
        if (ForwardInput is null) return false;
        if (!ExtensionCustomUiInputTranslator.TryTranslate(key, out var data)) return false;

        ForwardInput(data);
        return true;
    }

    protected override bool OnMouseEvent(MouseEventArgs args)
    {
        if (ForwardInput is null) return base.OnMouseEvent(args);

        SetFocus();

        if (args.Flags.HasFlag(MouseFlags.Button1Clicked))
        {
            ForwardInput(ExtensionCustomUiInputTranslator.TranslateMouseClickForTest(args.Position.X, args.Position.Y));
        }

        args.Handled = true;
        return true;
    }

    private void HandleFrameChanged()
    {
        if (_snapshot is null || ForwardResize is null) return;

        var width = Math.Max(1, Frame.Width);
        var height = Math.Max(1, Frame.Height);
        if (width == _lastForwardedWidth && height == _lastForwardedHeight) return;

        _lastForwardedWidth = width;
        _lastForwardedHeight = height;
        ForwardResize(width, height);
    }

    protected override bool OnDrawingText(DrawContext? context)
    {
        if (_styledRuns.Count == 0) return true;

        foreach (var run in _styledRuns)
        {
            if (run.Row >= Math.Max(1, Frame.Height)) break;

            Move(run.Column, run.Row);
            SetAttribute(run.Attribute);
            AddStr(run.Text);
        }

        return true;
    }

    private static IReadOnlyList<AnsiStyledRun> BuildRuns(IReadOnlyList<AnsiStyledText> lines)
    {
        var output = new List<AnsiStyledRun>();
        for (var row = 0; row < lines.Count; row++)
        {
            var column = 0;
            foreach (var run in lines[row].Runs)
            {
                output.Add(run with { Row = row, Column = column });
                column += run.Text.Length;
            }
        }

        return output;
    }
}
