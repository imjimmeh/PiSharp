using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public static class TuiShortcutKeyParser
{
    private static readonly IReadOnlyDictionary<string, Key> NamedKeys = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = Key.Esc,
        ["escape"] = Key.Esc,
        ["enter"] = Key.Enter,
        ["return"] = Key.Enter,
        ["tab"] = Key.Tab,
        ["backspace"] = Key.Backspace,
        ["delete"] = Key.Delete,
        ["del"] = Key.Delete,
        ["pageup"] = Key.PageUp,
        ["pgup"] = Key.PageUp,
        ["pagedown"] = Key.PageDown,
        ["pgdn"] = Key.PageDown,
        ["up"] = Key.CursorUp,
        ["down"] = Key.CursorDown,
        ["left"] = Key.CursorLeft,
        ["right"] = Key.CursorRight,
        ["end"] = Key.End,
        ["home"] = Key.Home
    };

    public static bool TryParse(string keys, out IReadOnlyList<Key> terminalKeys)
    {
        terminalKeys = [];
        if (string.IsNullOrWhiteSpace(keys)) return false;

        var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var ctrl = false;
        var alt = false;
        var shift = false;
        string? keyPart = null;

        foreach (var part in parts)
        {
            if (string.Equals(part, "ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
                continue;
            }
            if (string.Equals(part, "alt", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "option", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
                continue;
            }
            if (string.Equals(part, "shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
                continue;
            }

            if (keyPart is not null) return false;
            keyPart = part;
        }

        if (keyPart is null || !TryParseBaseKey(keyPart, out var key)) return false;

        if (ctrl) key = key.WithCtrl;
        if (alt) key = key.WithAlt;
        if (shift) key = key.WithShift;

        terminalKeys = ExpandTerminalKeyAliases(key).Distinct().ToArray();
        return true;
    }

    public static IReadOnlyList<Key> Parse(string keys)
        => TryParse(keys, out var terminalKeys)
            ? terminalKeys
            : throw new FormatException($"Unsupported shortcut key string '{keys}'.");

    public static IEnumerable<Key> ExpandTerminalKeyAliases(Key key)
    {
        yield return key;

        if (!key.IsCtrl || key.IsAlt || key.IsShift) yield break;

        var baseCode = key.KeyCode & KeyCode.CharMask;
        if (baseCode < KeyCode.A || baseCode > KeyCode.Z) yield break;

        var controlCode = (int)(baseCode - KeyCode.A) + 1;
        if (controlCode is 8 or 9 or 10 or 13) yield break;

        yield return new Key((KeyCode)controlCode);
    }

    private static bool TryParseBaseKey(string value, out Key key)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (NamedKeys.TryGetValue(normalized, out var namedKey))
        {
            key = namedKey;
            return true;
        }

        if (normalized.Length == 1)
        {
            var ch = normalized[0];
            if (char.IsLetter(ch))
            {
                key = new Key((KeyCode)((int)KeyCode.A + (char.ToUpperInvariant(ch) - 'A')));
                return true;
            }
            if (char.IsDigit(ch))
            {
                key = new Key((KeyCode)ch);
                return true;
            }
        }

        key = default!;
        return false;
    }
}
