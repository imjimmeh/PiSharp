using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class TerminalGuiApplicationContext : ITuiApplicationContext
{
    private readonly ITuiDispatcher _dispatcher;

    public TerminalGuiApplicationContext()
        : this(TerminalGuiDispatcher.Instance)
    {
    }

    public TerminalGuiApplicationContext(ITuiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action) => _dispatcher.Post(action);
    public object AddTimeout(TimeSpan interval, Func<bool> callback) => _dispatcher.AddTimeout(interval, callback);
    public void RemoveTimeout(object token) => _dispatcher.RemoveTimeout(token);
    public void RequestStop(Toplevel view) => Application.RequestStop(view);
    public void Run(Toplevel view) => Application.Run(view, null!);

    public event EventHandler<Key>? KeyDown
    {
        add => Application.KeyDown += value;
        remove => Application.KeyDown -= value;
    }

    public event EventHandler<SizeChangedEventArgs>? SizeChanging
    {
        add => Application.SizeChanging += value;
        remove => Application.SizeChanging -= value;
    }
}
