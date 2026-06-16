using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class TerminalGuiDispatcher : ITuiDispatcher
{
    public static readonly TerminalGuiDispatcher Instance = new();

    public void Post(Action action) => Application.Invoke(action);
    public object AddTimeout(TimeSpan interval, Func<bool> callback) => Application.AddTimeout(interval, callback)!;
    public void RemoveTimeout(object token) => Application.RemoveTimeout(token);
}
