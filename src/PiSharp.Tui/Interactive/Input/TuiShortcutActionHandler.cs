using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Agent.Harness;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.Shell;

namespace PiSharp.Tui.Interactive.Input;

internal sealed class TuiShortcutActionHandler
{
    private readonly ILogger<TuiShortcutActionHandler> _logger;
    private readonly TuiShellView _shell;
    private readonly TuiInlineSelectionCoordinator _inlineSelection;
    private readonly ITuiApplicationContext _appContext;
    private readonly TuiStateGateway _gateway;
    private readonly TuiSessionContext _session;
    private readonly TuiHostOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<string> _reportedShortcutErrors = new(StringComparer.Ordinal);
    private bool _ctrlCExitArmed;

    internal TuiCommandController CommandController { get; set; } = null!;

    internal TuiShortcutActionHandler(
        TuiShellView shell,
        TuiInlineSelectionCoordinator inlineSelection,
        ITuiApplicationContext appContext,
        TuiStateGateway gateway,
        TuiSessionContext session,
        TuiHostOptions options,
        CancellationToken cancellationToken,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<TuiShortcutActionHandler>() ?? NullLogger<TuiShortcutActionHandler>.Instance;
        _shell = shell;
        _inlineSelection = inlineSelection;
        _appContext = appContext;
        _gateway = gateway;
        _session = session;
        _options = options;
        _cancellationToken = cancellationToken;
    }

    internal void UpdateState(Func<TuiRenderState, TuiRenderState> update)
        => _gateway.Update(update);

    internal void HandleAbortShortcut()
    {
        if (!string.IsNullOrEmpty(_shell.Prompt.PromptText))
        {
            _shell.Prompt.ClearPrompt();
            _gateway.Update(s => s.SetEditorText(null));
            return;
        }

        if (_inlineSelection.CurrentSession is not null)
        {
            _inlineSelection.CancelInlineSelection();
            return;
        }

        if (_session.CurrentRuntime.Phase == AgentHarnessPhase.Idle) return;

        _session.AbortPending = true;
        _session.CurrentRuntime.Abort();
        _gateway.Set(_gateway.State.AppendSystem("Abort requested.",
            systemMessageTag: "abort",
            removeDelayAfterEvent: TimeSpan.FromSeconds(2),
            expiresAfter: TimeSpan.FromSeconds(30)) with
        {
            IsBusy = false,
            Status = "Idle"
        });
    }

    internal void HandleClearEditorShortcut()
    {
        if (_shell.Chat.HasSelection)
        {
            _shell.Chat.CopySelectionToClipboard();
            return;
        }

        _shell.Prompt.ClearPrompt();
        _gateway.Update(s => s.SetEditorText(null));
    }

    internal void HandleCtrlCShortcut()
    {
        _logger.LogDebug("Ctrl+C shortcut handler invoked promptLength={PromptLength} hasSelection={HasSelection} exitArmed={ExitArmed}",
            _shell.Prompt.PromptText.Length, _shell.Chat.HasSelection, _ctrlCExitArmed);

        if (_shell.Chat.HasSelection)
        {
            _ctrlCExitArmed = false;
            _shell.Chat.CopySelectionToClipboard();
            _logger.LogDebug("Ctrl+C copied selected transcript text to clipboard");
            return;
        }

        if (!string.IsNullOrEmpty(_shell.Prompt.PromptText))
        {
            _shell.Prompt.ClearPrompt();
            _gateway.Update(s => s.SetEditorText(null));
            _ctrlCExitArmed = true;
            _logger.LogDebug("Ctrl+C cleared non-empty prompt and armed second-press exit");
            return;
        }

        if (_ctrlCExitArmed)
        {
            _appContext.Post(() => _appContext.RequestStop(_shell.Window));
            _logger.LogDebug("Ctrl+C requested TUI stop on second empty-prompt press");
            return;
        }

        _ctrlCExitArmed = true;
        _logger.LogDebug("Ctrl+C armed empty-prompt exit without stopping");
    }

    internal void HandleCycleThinkingLevelShortcut()
    {
        if (_options.CycleThinkingLevelAsync is null)
        {
            _gateway.Update(s => s.AppendSystem("Thinking level cycling is unavailable in this mode.", true));
            return;
        }

        var task = _options.CycleThinkingLevelAsync(_cancellationToken);
        _ = HandleCycleThinkingLevelResultAsync(task);
    }

    private async Task HandleCycleThinkingLevelResultAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thinking level cycle failed");
            _gateway.Update(s => s.AppendSystem($"Error: {ex.Message}", true));
        }
    }

    internal void DispatchShortcutCommand(string command)
    {
        switch (command)
        {
            case "quit":
                _appContext.Post(() => _appContext.RequestStop(_shell.Window));
                return;
            case "toggle-left-sidebar":
                HandleToggleLeftSidebar();
                return;
            case "toggle-right-sidebar":
                HandleToggleRightSidebar();
                return;
            case "help":
                _ = CommandController.TryHandleCommandAsync("/help", _cancellationToken);
                return;
        }
        _ = CommandController.TryHandleCommandAsync(command, _cancellationToken);
    }

    internal void ReportShortcutError(string message)
    {
        if (!_reportedShortcutErrors.Add(message)) return;
        _appContext.Post(() => _gateway.Update(s => s.AppendSystem(message, isError: true)));
    }

    internal void HandleToggleLeftSidebar()
    {
        _gateway.Update(s => s.ToggleLeftSidebar());
        _shell.SetSidebarVisibility(_gateway.State.LeftSidebarVisible, _gateway.State.RightSidebarVisible);
    }

    internal void HandleToggleRightSidebar()
    {
        _gateway.Update(s => s.ToggleRightSidebar());
        _shell.SetSidebarVisibility(_gateway.State.LeftSidebarVisible, _gateway.State.RightSidebarVisible);
    }
}
