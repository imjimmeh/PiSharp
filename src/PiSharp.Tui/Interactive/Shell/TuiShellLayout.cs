using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Shell;

internal readonly record struct TuiShellLayoutMetrics(
    int HeaderHeight,
    int FooterHeight,
    int SuggestionsHeight,
    int WorkingIndicatorHeight,
    bool SuggestionsVisible,
    bool WorkingIndicatorVisible,
    int PromptHeight,
    int BottomReserved);

internal static class TuiShellLayout
{
    public static TuiShellLayoutMetrics CalculateMetrics(
        int headerHeight,
        int footerHeight,
        int suggestionsHeight,
        int workingIndicatorHeight,
        bool suggestionsVisible,
        bool workingIndicatorVisible,
        int promptHeight)
    {
        var bottomReserved = TuiLayoutMetrics.CalculateBottomReserved(
            footerHeight,
            suggestionsHeight,
            workingIndicatorHeight,
            suggestionsVisible,
            workingIndicatorVisible,
            promptHeight);

        return new TuiShellLayoutMetrics(
            headerHeight,
            footerHeight,
            suggestionsHeight,
            workingIndicatorHeight,
            suggestionsVisible,
            workingIndicatorVisible,
            promptHeight,
            bottomReserved);
    }
}
