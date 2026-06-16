using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

internal static class TuiKeyText
{
    public static bool TryGetPrintableText(Key key, out string text)
    {
        text = string.Empty;
        if (key.IsCtrl || key.IsAlt) return false;

        if (key.IsShift && TryGetShiftedPunctuation(key, out var shifted))
        {
            text = shifted;
            return true;
        }

        var rune = key.AsRune;
        if (rune.Value < ' ') return false;

        text = rune.ToString();
        return true;
    }

    private static bool TryGetShiftedPunctuation(Key key, out string text)
    {
        var baseCode = key.KeyCode & KeyCode.CharMask;
        text = baseCode switch
        {
            (KeyCode)'1' => "!",
            (KeyCode)'4' => "$",
            (KeyCode)'5' => "%",
            (KeyCode)'6' => "^",
            (KeyCode)'7' => "&",
            (KeyCode)'8' => "*",
            (KeyCode)'9' => "(",
            (KeyCode)'0' => ")",
            (KeyCode)'-' => "_",
            (KeyCode)'=' => "+",
            (KeyCode)'[' => "{",
            (KeyCode)']' => "}",
            (KeyCode)'\\' => "|",
            (KeyCode)';' => ":",
            (KeyCode)'\'' => "@",
            (KeyCode)',' => "<",
            (KeyCode)'.' => ">",
            (KeyCode)'/' => "?",
            _ => string.Empty
        };

        return text.Length > 0;
    }
}
