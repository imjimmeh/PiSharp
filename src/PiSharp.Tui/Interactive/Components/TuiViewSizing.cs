using PiSharp.Tui.Interactive.Rendering;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

internal static class TuiViewSizing
{
    public static int ResolveWidth(View view, int fallback)
    {
        if (view.Frame.Width > 0) return Math.Max(1, view.Frame.Width);

        if (view.Viewport.Width > 0) return Math.Max(1, view.Viewport.Width);

        var parentWidth = view.SuperView?.Frame.Width ?? 0;
        if (parentWidth > 0)
        {
            var x = Math.Max(0, view.Frame.X);
            return Math.Max(1, parentWidth - x);
        }

        try
        {
            if (Console.WindowWidth > 0) return Math.Max(1, Console.WindowWidth);
        }
        catch (IOException)
        {
            // Test runners and redirected hosts may not expose a console handle.
        }

        return Math.Max(1, fallback);
    }

    public static IReadOnlyList<string> WrapLines(IEnumerable<string> lines, int width)
        => lines.SelectMany(line => TuiRenderBuffer.Wrap(line ?? string.Empty, Math.Max(1, width))).ToArray();

    public static string WrappedText(IEnumerable<string> lines, int width)
        => string.Join('\n', WrapLines(lines, width));
}
