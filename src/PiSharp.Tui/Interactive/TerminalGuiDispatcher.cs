using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class TerminalGuiDispatcher : ITuiDispatcher
{
    private readonly Action<Action> _invoke;
    private readonly Action _wakeup;

    public static readonly TerminalGuiDispatcher Instance = new();

    public TerminalGuiDispatcher()
        : this(Application.Invoke, Application.Wakeup)
    {
    }

    internal TerminalGuiDispatcher(Action<Action> invoke, Action wakeup)
    {
        _invoke = invoke;
        _wakeup = wakeup;
    }

    public void Post(Action action)
    {
        _invoke(action);
        _wakeup();
    }

    public object AddTimeout(TimeSpan interval, Func<bool> callback) => Application.AddTimeout(interval, callback)!;
    public void RemoveTimeout(object token) => Application.RemoveTimeout(token);
}
