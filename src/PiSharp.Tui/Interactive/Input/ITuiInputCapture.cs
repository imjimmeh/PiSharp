using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Input;

internal interface ITuiInputCapture
{
    string Name { get; }
    bool TryHandleKey(Key key);
}
