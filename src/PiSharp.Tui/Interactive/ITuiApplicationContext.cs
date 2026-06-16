using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public interface ITuiApplicationContext : ITuiDispatcher
{
    void RequestStop(Toplevel view);
    void Run(Toplevel view);
    event EventHandler<Key>? KeyDown;
    event EventHandler<SizeChangedEventArgs>? SizeChanging;
}
