using System.Text;

namespace PiSharp.Tui.Interactive.Rendering;

public static class TuiCellWidth
{
    private const int TabStopWidth = 4;

    public static bool IsWideRune(int value)
        => value is >= 0x1100 and <= 0x115F
            or 0x2329 or 0x232A
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;

    public static int RuneCellWidth(Rune rune, int currentColumn = 0)
    {
        if (rune.Value == '\t')
        {
            var remainder = currentColumn % TabStopWidth;
            return remainder == 0 ? TabStopWidth : TabStopWidth - remainder;
        }

        if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark or System.Globalization.UnicodeCategory.Format)
            return 0;

        return IsWideRune(rune.Value) ? 2 : 1;
    }

    public static int CellColumnToTextIndex(string text, int column)
    {
        if (column <= 0 || string.IsNullOrEmpty(text)) return 0;

        var cells = 0;
        for (var index = 0; index < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, index);
            var width = RuneCellWidth(rune, cells);
            if (column <= cells) return index;
            if (column < cells + width) return index;

            cells += width;
            index += rune.Utf16SequenceLength;
        }

        return text.Length;
    }
}
