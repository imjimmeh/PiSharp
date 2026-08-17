using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class TerminalGuiApplicationContext : ITuiApplicationContext
{
    private readonly ITuiDispatcher _dispatcher;
    private readonly ILogger _logger;

    public TerminalGuiApplicationContext()
        : this(TerminalGuiDispatcher.Instance)
    {
    }

    public TerminalGuiApplicationContext(ITuiDispatcher dispatcher, ILoggerFactory? loggerFactory = null)
    {
        _dispatcher = dispatcher;
        _logger = loggerFactory?.CreateLogger("TerminalGuiApplicationContext") ?? NullLogger.Instance;
    }

    public void Post(Action action)
    {
        _logger.LogDebug("TerminalGuiApplicationContext.Post entry");
        _dispatcher.Post(action);
        _logger.LogDebug("TerminalGuiApplicationContext.Post exit");
    }
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
