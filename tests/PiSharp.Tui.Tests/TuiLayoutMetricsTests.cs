using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiLayoutMetricsTests
{
    [Fact]
    public void TextLineCountCountsWrappedTextRowsWithMinimumOne()
    {
        var empty = new TextView();
        var multi = new TextView { Text = "one\ntwo\nthree" };

        Assert.Equal(1, TuiLayoutMetrics.TextLineCount(empty));
        Assert.Equal(3, TuiLayoutMetrics.TextLineCount(multi));
    }

    [Fact]
    public void CalculateBottomReservedIncludesVisibleDynamicSections()
    {
        var reserved = TuiLayoutMetrics.CalculateBottomReserved(
            4,
            3,
            2,
            true,
            true,
            5);

        Assert.Equal(16, reserved);
    }

    [Fact]
    public void PromptHeightGrowsWithMultilinePromptText()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("one\ntwo\nthree\nfour");

        Assert.Equal(4, TuiLayoutMetrics.PromptContentHeight(prompt));
    }
}
