using System.Text;
using PiSharp.Tui.Interactive.Rendering;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiCellWidthTests
{
    [Theory]
    [InlineData('a', 1)]
    [InlineData('z', 1)]
    [InlineData('A', 1)]
    [InlineData('Z', 1)]
    [InlineData('0', 1)]
    [InlineData(' ', 1)]
    [InlineData('\n', 1)]
    public void RuneCellWidthReturnsOneForPlainAscii(char ch, int expectedWidth)
    {
        var rune = new Rune(ch);

        var width = TuiCellWidth.RuneCellWidth(rune);

        Assert.Equal(expectedWidth, width);
    }

    [Theory]
    [InlineData('\t', 0, 4)]
    [InlineData('\t', 1, 3)]
    [InlineData('\t', 2, 2)]
    [InlineData('\t', 3, 1)]
    [InlineData('\t', 4, 4)]
    [InlineData('\t', 5, 3)]
    [InlineData('\t', 6, 2)]
    [InlineData('\t', 7, 1)]
    [InlineData('\t', 8, 4)]
    public void RuneCellWidthReturnsTabStopWidth(char ch, int column, int expectedWidth)
    {
        var rune = new Rune(ch);

        var width = TuiCellWidth.RuneCellWidth(rune, column);

        Assert.Equal(expectedWidth, width);
    }

    [Theory]
    [InlineData(0x4E16)]  // 世
    [InlineData(0x754C)]  // 界
    [InlineData(0x6C49)]  // 汉
    [InlineData(0x5B57)]  // 字
    [InlineData(0xAC00)]  // 가 (Hangul)
    [InlineData(0xD55C)]  // 한
    [InlineData(0x3042)]  // あ (Hiragana - not in wide ranges, but many east asian are)
    public void RuneCellWidthReturnsTwoForWideCharacters(int codePoint)
    {
        var rune = new Rune((uint)codePoint);

        var width = TuiCellWidth.RuneCellWidth(rune);

        Assert.Equal(2, width);
    }

    [Theory]
    [InlineData(0x0301)]  // combining acute accent
    [InlineData(0x0308)]  // combining diaeresis
    [InlineData(0x0327)]  // combining cedilla
    public void RuneCellWidthReturnsZeroForCombiningMarks(int codePoint)
    {
        var rune = new Rune((uint)codePoint);

        var width = TuiCellWidth.RuneCellWidth(rune);

        Assert.Equal(0, width);
    }

    [Fact]
    public void RuneCellWidthReturnsTwoForEmoji()
    {
        var rune = new Rune(0x1F600); // 😀

        var width = TuiCellWidth.RuneCellWidth(rune);

        Assert.Equal(2, width);
    }

    [Theory]
    [InlineData(0x1100, true)]   // Hangul Choseong
    [InlineData(0x115F, true)]
    [InlineData(0x2329, true)]
    [InlineData(0x232A, true)]
    [InlineData(0x2E80, true)]   // CJK Radical
    [InlineData(0xA4CF, true)]
    [InlineData(0xAC00, true)]   // Hangul
    [InlineData(0xD7A3, true)]
    [InlineData(0xF900, true)]   // CJK Compat
    [InlineData(0xFE10, true)]   // Vertical forms
    [InlineData(0xFF00, true)]   // Halfwidth/Fullwidth
    [InlineData(0xFF60, true)]
    [InlineData(0xFFE0, true)]
    [InlineData(0xFFE6, true)]
    [InlineData(0x1F300, true)]  // Misc Symbols and Pictographs
    [InlineData(0x1FAFF, true)]
    [InlineData(0x20000, true)]  // CJK Extension B
    [InlineData(0x3FFFD, true)]
    [InlineData(0x0041, false)]  // 'A'
    [InlineData(0x0061, false)]  // 'a'
    [InlineData(0x0020, false)]  // space
    [InlineData(0x00E9, false)]  // é
    [InlineData(0x0301, false)]  // combining mark
    public void IsWideRuneCorrectlyClassifiesCodePoints(int codePoint, bool expected)
    {
        var result = TuiCellWidth.IsWideRune(codePoint);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("hello", 0, 0)]
    [InlineData("hello", 1, 1)]
    [InlineData("hello", 5, 5)]
    [InlineData("hello", 10, 5)]
    [InlineData("", 0, 0)]
    [InlineData("", 5, 0)]
    [InlineData("a\tb", 0, 0)]
    [InlineData("a\tb", 1, 1)]
    [InlineData("a\tb", 4, 2)]
    [InlineData("a\tb", 5, 3)]
    public void CellColumnToTextIndexConvertsColumnsToOffsets(string text, int column, int expectedOffset)
    {
        var offset = TuiCellWidth.CellColumnToTextIndex(text, column);

        Assert.Equal(expectedOffset, offset);
    }

    [Fact]
    public void CellColumnToTextIndexHandlesWideCharacters()
    {
        var text = "a界b"; // 'a'(1 cell) + '界'(2 cells) + 'b'(1 cell) = 4 cells

        Assert.Equal(1, TuiCellWidth.CellColumnToTextIndex(text, 1));
        Assert.Equal(1, TuiCellWidth.CellColumnToTextIndex(text, 2));
        Assert.Equal(2, TuiCellWidth.CellColumnToTextIndex(text, 3));
        Assert.Equal(3, TuiCellWidth.CellColumnToTextIndex(text, 4));
        Assert.Equal(3, TuiCellWidth.CellColumnToTextIndex(text, 5));
    }

    [Fact]
    public void CellColumnToTextIndexHandlesCombiningMarks()
    {
        var text = "e" + char.ConvertFromUtf32(0x0301); // e + combining acute = 1 cell

        Assert.Equal(0, TuiCellWidth.CellColumnToTextIndex(text, 0));
        Assert.Equal(1, TuiCellWidth.CellColumnToTextIndex(text, 1));
    }
}
