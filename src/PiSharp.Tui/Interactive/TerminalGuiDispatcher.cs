using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class TerminalGuiDispatcher : ITuiDispatcher
{
    private readonly Action<Action> _invoke;
    private readonly Action _wakeup;
    private readonly ILogger _logger;

    public static readonly TerminalGuiDispatcher Instance = new();

    public TerminalGuiDispatcher()
        : this(Application.Invoke, Application.Wakeup)
    {
    }

    internal TerminalGuiDispatcher(Action<Action> invoke, Action wakeup, ILoggerFactory? loggerFactory = null)
    {
        _invoke = invoke;
        _wakeup = wakeup;
        _logger = loggerFactory?.CreateLogger("TerminalGuiDispatcher") ?? NullLogger.Instance;
    }

    public void Post(Action action)
    {
        _logger.LogDebug("TerminalGuiDispatcher.Post entry");
        _invoke(action);
        _wakeup();
        _logger.LogDebug("TerminalGuiDispatcher.Post exit");
    }

    public object AddTimeout(TimeSpan interval, Func<bool> callback) => Application.AddTimeout(interval, callback)!;
    public void RemoveTimeout(object token) => Application.RemoveTimeout(token);
}
