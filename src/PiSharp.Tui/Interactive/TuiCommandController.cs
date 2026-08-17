using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Agent.Harness;

namespace PiSharp.Tui.Interactive;

public sealed record TuiCommandControllerOptions(
    Func<TuiRenderState> GetState,
    Action<TuiRenderState> SetState,
    Action RequestAbort,
    Action RequestExit,
    Func<string> RenderHotkeysText,
    Func<string, TuiCommandDispatchRequest>? CreateDispatchRequest = null,
    Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>>? DispatchCommandAsync = null,
    Func<CancellationToken, Task>? RefreshAfterPossibleSessionChangeAsync = null,
    Func<AgentHarnessPhase>? GetCurrentPhase = null,
    Action? OnAbortRequested = null,
    Func<Func<TuiRenderState, TuiRenderState>, TuiRenderState>? UpdateState = null);

public sealed class TuiCommandController(TuiCommandControllerOptions options, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger<TuiCommandController> _logger = loggerFactory?.CreateLogger<TuiCommandController>() ?? NullLogger<TuiCommandController>.Instance;
    private bool _commandInProgress;

    public bool IsCommandInProgress => _commandInProgress;

    public async Task<bool> TryHandleCommandAsync(string text, CancellationToken cancellationToken)
    {
        if (!text.StartsWith("/", StringComparison.Ordinal)) return false;
        _logger.LogDebug("TUI command received text={CommandText} inProgress={CommandInProgress}", text, _commandInProgress);

        var command = text.Trim();

        if (string.Equals(command, "/help", StringComparison.OrdinalIgnoreCase))
        {
            var slashCommands = TuiKeybindings.CommandDescriptors
                .Where(d => d.SlashCommand is not null)
                .Select(d => d.SlashCommand!)
                .ToArray();
            var slashList = slashCommands.Length > 0 ? string.Join(", ", slashCommands) : "/help, /hotkeys";
            SetState(state => state.AppendSystem($"Commands: {slashList}.\n{options.RenderHotkeysText()}"));
            return true;
        }
        if (string.Equals(command, "/hotkeys", StringComparison.OrdinalIgnoreCase))
        {
            SetState(state => state.AppendSystem(options.RenderHotkeysText()));
            return true;
        }
        if (string.Equals(command, "/clear", StringComparison.OrdinalIgnoreCase))
        {
            SetState(state => state.ClearTranscript());
            return true;
        }
        if (string.Equals(command, "/abort", StringComparison.OrdinalIgnoreCase))
        {
            options.OnAbortRequested?.Invoke();
            options.RequestAbort();
            SetState(state => state.AppendSystem("Abort requested.",
                systemMessageTag: "abort",
                removeDelayAfterEvent: TimeSpan.FromSeconds(2),
                expiresAfter: TimeSpan.FromSeconds(30)));
            return true;
        }
        if (string.Equals(command, "/exit", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("TUI command requested local exit text={CommandText}", command);
            options.RequestExit();
            return true;
        }

        if (options.DispatchCommandAsync is null || options.CreateDispatchRequest is null)
        {
            _logger.LogDebug("TUI command has no dispatcher and is unknown text={CommandText}", command);
            SetState(state => state.AppendSystem($"Unknown command: {command}. Type /help for available commands.", true));
            return true;
        }

        _commandInProgress = true;
        _logger.LogDebug("TUI command dispatch started text={CommandText}", command);
        if (IsResumeCommand(command)) SetState(TuiSessionSwitch.BeginResumeLoading);

        _ = RunCommandAsync(command, cancellationToken);
        return true;
    }

    private async Task<TuiCommandDispatchResult> RunCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        try
        {
            var dispatchTask = RunWithoutSynchronizationContext(() =>
                options.DispatchCommandAsync!(options.CreateDispatchRequest!(commandText), cancellationToken));
            var dispatchResult = await dispatchTask.ConfigureAwait(false);
            _logger.LogDebug("TUI command dispatch completed text={CommandText} shouldExit={ShouldExit}", commandText, dispatchResult.ShouldExit);
            if (options.RefreshAfterPossibleSessionChangeAsync is not null && IsSessionChangingCommand(commandText))
                await options.RefreshAfterPossibleSessionChangeAsync(cancellationToken).ConfigureAwait(false);
            if (dispatchResult.ShouldExit) options.RequestExit();
            return dispatchResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TuiCommandDispatchResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command {CommandText} dispatch failed", commandText);
            SetState(state => state.AppendSystem($"Error: {ex.Message}", true));
            return new TuiCommandDispatchResult(true);
        }
        finally
        {
            if (options.GetCurrentPhase?.Invoke() == AgentHarnessPhase.Idle)
            {
                SetState(TuiSessionSwitch.EndCommandLoading);
            }
            _commandInProgress = false;
        }
    }
    private void SetState(Func<TuiRenderState, TuiRenderState> update)
    {
        if (options.UpdateState is not null)
        {
            options.UpdateState(update);
            return;
        }
        options.SetState(update(options.GetState()));
    }

    private static T RunWithoutSynchronizationContext<T>(Func<T> work)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return work();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static string? GetCommandName(string command)
    {
        var trimmed = command.Trim();
        return trimmed.Length > 1
            ? trimmed[1..].Split([' '], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
    }

    private static bool IsResumeCommand(string command)
        => GetCommandName(command) is { } name
            && (string.Equals(name, "resume", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "session", StringComparison.OrdinalIgnoreCase));

    private static bool IsSessionChangingCommand(string command)
        => IsResumeCommand(command)
            || GetCommandName(command) is { } name
                && (string.Equals(name, "fork", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "clone", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "new", StringComparison.OrdinalIgnoreCase));
}
