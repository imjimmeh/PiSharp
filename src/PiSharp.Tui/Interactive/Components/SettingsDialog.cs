using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public static class SettingsDialog
{
    public static Task ShowAsync(string title, string content, CancellationToken cancellationToken = default, ITuiDispatcher? dispatcher = null)
    {
        var disp = dispatcher ?? TerminalGuiDispatcher.Instance;
        disp.Post(() => MessageBox.Query(title, content, "OK"));
        return Task.CompletedTask;
    }
}
