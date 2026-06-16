using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Tui.Interactive;

public interface ITerminalScreenSession : IDisposable
{
    void Enter();

    void RestoreBracketedPaste();

    void Exit();
}

internal interface ITerminalSessionLifetimeEvents
{
    IDisposable Register(Action cleanup);
}

internal interface ITerminalCancelKeyPressEvent
{
    bool Cancel { get; set; }
}

internal interface ITerminalControlInputMode
{
    bool TreatControlCAsInput { get; set; }
}

internal sealed class ConsoleTerminalControlInputMode : ITerminalControlInputMode
{
    public static readonly ConsoleTerminalControlInputMode Instance = new();

    private ConsoleTerminalControlInputMode()
    {
    }

    public bool TreatControlCAsInput
    {
        get
        {
            try
            {
                return Console.TreatControlCAsInput;
            }
            catch (IOException)
            {
                // Test runners and redirected hosts may not expose a valid console input handle.
                return false;
            }
        }
        set
        {
            try
            {
                Console.TreatControlCAsInput = value;
            }
            catch (IOException)
            {
                // Test runners and redirected hosts may not expose a valid console input handle.
            }
        }
    }
}

internal sealed class ConsoleTerminalSessionLifetimeEvents : ITerminalSessionLifetimeEvents
{
    public static readonly ConsoleTerminalSessionLifetimeEvents Instance = new();
    public static ILoggerFactory? LoggerFactory { get; set; }

    private static ILogger Logger => LoggerFactory?.CreateLogger(nameof(ConsoleTerminalSessionLifetimeEvents)) ?? NullLogger.Instance;

    private ConsoleTerminalSessionLifetimeEvents()
    {
    }

    public IDisposable Register(Action cleanup)
    {
        ConsoleCancelEventHandler cancelKeyPressHandler = (_, args) => HandleCancelKeyPress(cleanup, new TerminalCancelKeyPressEvent(args));
        EventHandler processExitHandler = (_, _) => cleanup();
        UnhandledExceptionEventHandler unhandledExceptionHandler = (_, _) => cleanup();

        Console.CancelKeyPress += cancelKeyPressHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += unhandledExceptionHandler;

        return new Registration(cancelKeyPressHandler, processExitHandler, unhandledExceptionHandler);
    }

    internal static void HandleCancelKeyPress(Action cleanup, ITerminalCancelKeyPressEvent args)
    {
        args.Cancel = true;
        Logger.LogDebug("Cancelled Console.CancelKeyPress so Ctrl+C remains a TUI input event");
    }

    private sealed class TerminalCancelKeyPressEvent(ConsoleCancelEventArgs args) : ITerminalCancelKeyPressEvent
    {
        public bool Cancel
        {
            get => args.Cancel;
            set => args.Cancel = value;
        }
    }

    private sealed class Registration(
        ConsoleCancelEventHandler cancelKeyPressHandler,
        EventHandler processExitHandler,
        UnhandledExceptionEventHandler unhandledExceptionHandler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;

            Console.CancelKeyPress -= cancelKeyPressHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            AppDomain.CurrentDomain.UnhandledException -= unhandledExceptionHandler;
            _disposed = true;
        }
    }
}

public sealed class AnsiTerminalScreenSession : ITerminalScreenSession
{
    public const string EnterAlternateScreen = "\u001b[?1049h";
    public const string LeaveAlternateScreen = "\u001b[?1049l";
    public const string EnableBracketedPaste = "\u001b[?2004h";
    public const string DisableBracketedPaste = "\u001b[?2004l";
    public const string DisableX10MouseTracking = "\u001b[?9l";
    public const string DisableNormalMouseTracking = "\u001b[?1000l";
    public const string DisableButtonEventMouseTracking = "\u001b[?1002l";
    public const string DisableAnyEventMouseTracking = "\u001b[?1003l";
    public const string DisableUtf8MouseEncoding = "\u001b[?1005l";
    public const string DisableSgrMouseEncoding = "\u001b[?1006l";
    public const string DisableUrxvtMouseEncoding = "\u001b[?1015l";
    public const string CursorHome = "\u001b[H";
    public const string ClearScreen = "\u001b[2J";
    public const string HideCursor = "\u001b[?25l";
    public const string ShowCursor = "\u001b[?25h";

    private readonly TextWriter _writer;
    private readonly ITerminalSessionLifetimeEvents _lifetimeEvents;
    private readonly ITerminalControlInputMode _controlInputMode;
    private readonly ILogger<AnsiTerminalScreenSession> _logger;
    private readonly object _sync = new();
    private bool _entered;
    private bool? _previousTreatControlCAsInput;
    private IDisposable? _lifetimeRegistration;

    public AnsiTerminalScreenSession(TextWriter writer)
        : this(writer, ConsoleTerminalSessionLifetimeEvents.Instance, ConsoleTerminalControlInputMode.Instance, null)
    {
    }

    internal AnsiTerminalScreenSession(TextWriter writer, ITerminalSessionLifetimeEvents lifetimeEvents)
        : this(writer, lifetimeEvents, ConsoleTerminalControlInputMode.Instance, null)
    {
    }

    internal AnsiTerminalScreenSession(TextWriter writer, ITerminalSessionLifetimeEvents lifetimeEvents, ITerminalControlInputMode controlInputMode)
        : this(writer, lifetimeEvents, controlInputMode, null)
    {
    }

    internal AnsiTerminalScreenSession(TextWriter writer, ITerminalSessionLifetimeEvents lifetimeEvents, ITerminalControlInputMode controlInputMode, ILoggerFactory? loggerFactory)
    {
        _writer = writer;
        _lifetimeEvents = lifetimeEvents;
        _controlInputMode = controlInputMode;
        _logger = loggerFactory?.CreateLogger<AnsiTerminalScreenSession>() ?? NullLogger<AnsiTerminalScreenSession>.Instance;
    }

    public static AnsiTerminalScreenSession CreateDefault(ILoggerFactory? loggerFactory = null)
        => new(Console.Out, ConsoleTerminalSessionLifetimeEvents.Instance, ConsoleTerminalControlInputMode.Instance, loggerFactory);

    public void Enter()
    {
        lock (_sync)
        {
            if (_entered) return;

            _previousTreatControlCAsInput = _controlInputMode.TreatControlCAsInput;
            _controlInputMode.TreatControlCAsInput = true;
            _logger.LogDebug("Entered TUI terminal session and set TreatControlCAsInput=true previous={PreviousTreatControlCAsInput}", _previousTreatControlCAsInput);
            _entered = true;
            _lifetimeRegistration = _lifetimeEvents.Register(TryExit);
            _writer.Write(EnterAlternateScreen);
            _writer.Write(EnableBracketedPaste);
            _writer.Write(CursorHome);
            _writer.Write(ClearScreen);
            _writer.Flush();
        }
    }

    public void RestoreBracketedPaste()
    {
        lock (_sync)
        {
            if (!_entered) return;

            _controlInputMode.TreatControlCAsInput = true;
            _logger.LogDebug("Reasserted TreatControlCAsInput=true after terminal driver initialization");
            _writer.Write(EnableBracketedPaste);
            _writer.Flush();
        }
    }

    public void Exit()
    {
        IDisposable? lifetimeRegistration;
        lock (_sync)
        {
            if (!_entered) return;

            _writer.Write(DisableBracketedPaste);
            _writer.Write(DisableX10MouseTracking);
            _writer.Write(DisableNormalMouseTracking);
            _writer.Write(DisableButtonEventMouseTracking);
            _writer.Write(DisableAnyEventMouseTracking);
            _writer.Write(DisableUtf8MouseEncoding);
            _writer.Write(DisableSgrMouseEncoding);
            _writer.Write(DisableUrxvtMouseEncoding);
            _writer.Write(ShowCursor);
            _writer.Write(LeaveAlternateScreen);
            _writer.Flush();
            RestoreControlInputMode();
            _entered = false;
            lifetimeRegistration = _lifetimeRegistration;
            _lifetimeRegistration = null;
        }

        lifetimeRegistration?.Dispose();
    }

    private void TryExit()
    {
        try
        {
            Exit();
        }
        catch (IOException)
        {
            // Process shutdown cleanup is best-effort; the terminal may already be gone.
        }
        catch (ObjectDisposedException)
        {
            // Process shutdown cleanup is best-effort; the output stream may already be disposed.
        }
    }

    private void RestoreControlInputMode()
    {
        if (_previousTreatControlCAsInput is not { } previous) return;

        _controlInputMode.TreatControlCAsInput = previous;
        _logger.LogDebug("Restored TreatControlCAsInput={PreviousTreatControlCAsInput} on TUI terminal exit", previous);
        _previousTreatControlCAsInput = null;
    }

    public void Dispose()
        => Exit();
}
