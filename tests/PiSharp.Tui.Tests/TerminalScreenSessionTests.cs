using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TerminalScreenSessionTests
{
    [Fact]
    public void EnterSwitchesToAlternateScreenAndClearsFromHome()
    {
        using var writer = new StringWriter();
        var session = new AnsiTerminalScreenSession(writer);

        session.Enter();

        Assert.Equal("\u001b[?1049h\u001b[?2004h\u001b[H\u001b[2J", writer.ToString());
    }

    [Fact]
    public void EnterDoesNotEnableMouseTrackingByDefaultSoTerminalSelectionCanCopyText()
    {
        using var writer = new StringWriter();
        var session = new AnsiTerminalScreenSession(writer);

        session.Enter();

        Assert.DoesNotContain("\u001b[?100", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EnterDoesNotGloballyHideCursorSoFocusedPromptCanShowCaret()
    {
        using var writer = new StringWriter();
        var session = new AnsiTerminalScreenSession(writer);

        session.Enter();

        Assert.DoesNotContain(AnsiTerminalScreenSession.HideCursor, writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreBracketedPasteReenablesPasteModeAfterTerminalGuiInitialization()
    {
        using var writer = new StringWriter();
        var session = new AnsiTerminalScreenSession(writer);
        session.Enter();
        writer.GetStringBuilder().Clear();

        session.RestoreBracketedPaste();

        Assert.Equal(AnsiTerminalScreenSession.EnableBracketedPaste, writer.ToString());
    }

    [Fact]
    public void RestoreBracketedPasteReassertsCtrlCInputModeAfterTerminalGuiInitialization()
    {
        using var writer = new StringWriter();
        var controlInput = new RecordingTerminalControlInputMode(treatControlCAsInput: false);
        var session = new AnsiTerminalScreenSession(writer, new RecordingTerminalSessionLifetimeEvents(), controlInput);
        session.Enter();
        controlInput.TreatControlCAsInput = false;

        session.RestoreBracketedPaste();

        Assert.True(controlInput.TreatControlCAsInput);
    }

    [Fact]
    public void RestoreBracketedPasteDoesNothingBeforeEnter()
    {
        using var writer = new StringWriter();
        var session = new AnsiTerminalScreenSession(writer);

        session.RestoreBracketedPaste();

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void ExitShowsCursorAndRestoresPrimaryScreen()
    {
        using var writer = new StringWriter();
        var session = new AnsiTerminalScreenSession(writer);
        session.Enter();
        writer.GetStringBuilder().Clear();

        session.Exit();

        var output = writer.ToString();
        Assert.StartsWith(AnsiTerminalScreenSession.DisableBracketedPaste, output, StringComparison.Ordinal);
        Assert.Contains(AnsiTerminalScreenSession.ShowCursor, output, StringComparison.Ordinal);
        Assert.EndsWith(AnsiTerminalScreenSession.LeaveAlternateScreen, output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitDisablesMouseTrackingModesEnabledByTerminalGui()
    {
        using var writer = new StringWriter();
        var session = new AnsiTerminalScreenSession(writer);
        session.Enter();
        writer.GetStringBuilder().Clear();

        session.Exit();

        var output = writer.ToString();
        Assert.Contains("\u001b[?9l", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[?1000l", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[?1002l", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[?1003l", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[?1005l", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[?1006l", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[?1015l", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelKeyPressRestoresTerminalState()
    {
        using var writer = new StringWriter();
        var lifetimeEvents = new RecordingTerminalSessionLifetimeEvents();
        var session = new AnsiTerminalScreenSession(writer, lifetimeEvents);
        session.Enter();
        writer.GetStringBuilder().Clear();

        lifetimeEvents.RaiseCancelKeyPress();

        Assert.Contains("\u001b[?1006l", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains(AnsiTerminalScreenSession.LeaveAlternateScreen, writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CancelKeyPressCancelsConsoleInterruptWithoutRestoringTerminalState()
    {
        var cleanupCalled = false;
        var cancelEvent = new RecordingCancelKeyPressEvent();

        ConsoleTerminalSessionLifetimeEvents.HandleCancelKeyPress(() => cleanupCalled = true, cancelEvent);

        Assert.True(cancelEvent.Cancel);
        Assert.False(cleanupCalled);
    }

    [Fact]
    public void EnterTreatsCtrlCAsInputAndExitRestoresPreviousConsoleMode()
    {
        using var writer = new StringWriter();
        var controlInput = new RecordingTerminalControlInputMode(treatControlCAsInput: false);
        var session = new AnsiTerminalScreenSession(writer, new RecordingTerminalSessionLifetimeEvents(), controlInput);

        session.Enter();

        Assert.True(controlInput.TreatControlCAsInput);

        session.Exit();

        Assert.False(controlInput.TreatControlCAsInput);
    }

    private sealed class RecordingCancelKeyPressEvent : ITerminalCancelKeyPressEvent
    {
        public bool Cancel { get; set; }
    }

    private sealed class RecordingTerminalControlInputMode(bool treatControlCAsInput) : ITerminalControlInputMode
    {
        public bool TreatControlCAsInput { get; set; } = treatControlCAsInput;
    }

    private sealed class RecordingTerminalSessionLifetimeEvents : ITerminalSessionLifetimeEvents
    {
        private Action? _cleanup;

        public IDisposable Register(Action cleanup)
        {
            _cleanup = cleanup;
            return new Registration(() => _cleanup = null);
        }

        public void RaiseCancelKeyPress()
            => _cleanup?.Invoke();

        private sealed class Registration(Action dispose) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;

                dispose();
                _disposed = true;
            }
        }
    }
}
