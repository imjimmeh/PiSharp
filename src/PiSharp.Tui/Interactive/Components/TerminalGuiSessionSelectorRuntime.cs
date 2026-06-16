using PiSharp.Tui.Interactive.Theme;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

internal sealed class TerminalGuiSessionSelectorRuntime : ISessionSelectorRuntime
{
    private AnsiTerminalScreenSession? _terminalScreenSession;
    private bool _initialized;

    public void Enter()
    {
        if (_initialized || _terminalScreenSession is not null)
            throw new InvalidOperationException("Already entered.");
        _terminalScreenSession = AnsiTerminalScreenSession.CreateDefault();
        _terminalScreenSession.Enter();
        var driverName = TuiConsoleDriverName.DefaultForCurrentPlatform();
        TuiConsoleDriverName.PrepareConsoleForDriver(driverName);
        Application.Init(null!, driverName);
        _initialized = true;
        Theme.TuiTheme.Apply(null);
    }

    public void Exit()
    {
        if (_initialized)
        {
            Application.Shutdown();
            _initialized = false;
        }
        _terminalScreenSession?.Exit();
        _terminalScreenSession = null;
    }
}
