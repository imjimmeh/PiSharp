using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public static class TuiLayoutMetrics
{
    public const int PromptTitleHeight = 1;
    public const int PromptHeight = 3;
    public const int MaxPromptHeight = 8;
    public const int PromptBorderHeight = 1;

    public static int TextLineCount(View view)
        => Math.Max(1, (view.Text?.ToString() ?? string.Empty).Split('\n').Length);

    public static int PromptContentHeight(View prompt)
        => Math.Clamp(TextLineCount(prompt), PromptHeight, MaxPromptHeight);

    public static int CalculateBottomReserved(
        int footerHeight,
        int suggestionsHeight,
        int workingIndicatorHeight,
        bool suggestionsVisible,
        bool workingIndicatorVisible)
        => CalculateBottomReserved(
            footerHeight,
            suggestionsHeight,
            workingIndicatorHeight,
            suggestionsVisible,
            workingIndicatorVisible,
            PromptHeight);

    public static int CalculateBottomReserved(
        int footerHeight,
        int suggestionsHeight,
        int workingIndicatorHeight,
        bool suggestionsVisible,
        bool workingIndicatorVisible,
        int promptHeight)
        => Math.Max(1, footerHeight)
            + PromptBorderHeight
            + Math.Clamp(promptHeight, PromptHeight, MaxPromptHeight)
            + PromptTitleHeight
            + (suggestionsVisible ? Math.Max(1, suggestionsHeight) : 0)
            + (workingIndicatorVisible ? Math.Max(1, workingIndicatorHeight) : 0);
}
