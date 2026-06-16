using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public static class SessionTreeDialog
{
    public static Task ShowAsync(string tree, CancellationToken cancellationToken = default, ITuiDispatcher? dispatcher = null)
    {
        var disp = dispatcher ?? TerminalGuiDispatcher.Instance;
        disp.Post(() => MessageBox.Query("Session tree", tree, "OK"));
        return Task.CompletedTask;
    }
}
