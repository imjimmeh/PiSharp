using System.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Harness;
using PiSharp.Tui.Interactive.Rendering;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.Shell;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

internal sealed class TuiTranscriptInteractionController
{
    private readonly ILogger<TuiTranscriptInteractionController> _logger;
    private readonly TuiShellView _shell;
    private readonly TuiHostOptions _options;
    private readonly ITuiApplicationContext _appContext;
    private readonly TuiRenderCoordinator _renderCoordinator;
    private readonly TuiStateGateway _gateway;
    private readonly TuiSessionContext _session;
    private readonly Func<CancellationToken, Task<TuiSessionSnapshot?>> _loadSessionSnapshotAsync;
    private readonly Action<TuiSessionSnapshot, bool> _applySessionSnapshot;
    private readonly CancellationToken _cancellationToken;

    internal TuiTranscriptInteractionController(
        TuiShellView shell,
        TuiHostOptions options,
        ITuiApplicationContext appContext,
        TuiRenderCoordinator renderCoordinator,
        TuiStateGateway gateway,
        TuiSessionContext session,
        Func<CancellationToken, Task<TuiSessionSnapshot?>> loadSessionSnapshotAsync,
        Action<TuiSessionSnapshot, bool> applySessionSnapshot,
        CancellationToken cancellationToken,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<TuiTranscriptInteractionController>() ?? NullLogger<TuiTranscriptInteractionController>.Instance;
        _shell = shell;
        _options = options;
        _appContext = appContext;
        _renderCoordinator = renderCoordinator;
        _gateway = gateway;
        _session = session;
        _loadSessionSnapshotAsync = loadSessionSnapshotAsync;
        _applySessionSnapshot = applySessionSnapshot;
        _cancellationToken = cancellationToken;
    }

    internal void HandleChatInteraction(TuiInteractionHit hit)
    {
        if (string.Equals(hit.Target.Kind, "tool", StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(hit.Target.Action) || string.Equals(hit.Target.Action, "toggle", StringComparison.Ordinal)))
        {
            _gateway.Update(s => s.ToggleToolExpanded(hit.Target.Id));
        }
        _shell.Prompt.FocusAtEnd();
    }

    internal void HandleChatContextMenu(TuiInteractionHit hit)
    {
        if (!string.Equals(hit.Target.Kind, "message", StringComparison.Ordinal)) return;
        var item = _gateway.State.FindTranscriptItemByEntryId(hit.Target.Id);
        if (item is null) return;

        var canFork = _options.ForkFromEntryAsync is not null && _session.CurrentRuntime.Phase == AgentHarnessPhase.Idle;
        var copyItem = new MenuItem("_Copy message", "Copy message text", () => CopyMessageToClipboard(item));
        var forkItem = new MenuItem("_Fork from message", "Fork conversation from this message", () =>
        {
            if (!canFork) return;
            _ = ForkFromMessageAsync(hit.Target.Id);
        }, () => canFork);
        var menuItems = new[] { copyItem, forkItem };
        var chatFrame = _shell.Chat.Frame;
        var contextMenu = new ContextMenu
        {
            Host = _shell.Chat,
            Position = new Point(chatFrame.X + hit.Column, chatFrame.Y + hit.ViewRow)
        };
        contextMenu.Show(new MenuBarItem("_Message", menuItems));
        _shell.Prompt.FocusAtEnd();
    }

    internal void CopyMessageToClipboard(TuiTranscriptItem item)
    {
        var text = MessageText(item);
        var copied = false;
        try
        {
            copied = !string.IsNullOrEmpty(text) && _shell.Chat.ClipboardWriter(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clipboard write failed");
            copied = false;
        }

        var message = copied
            ? $"Copied {text.Length} message character{(text.Length == 1 ? string.Empty : "s")} to clipboard."
            : "Message text was empty, or the OS clipboard is unavailable.";
        _gateway.Update(s => s.AppendSystem(message, isError: !copied));
    }

    internal async Task ForkFromMessageAsync(string entryId)
    {
        if (_options.ForkFromEntryAsync is null)
        {
            _gateway.Update(s => s.AppendSystem("Forking from chat rows is unavailable in this mode.", true));
            return;
        }

        try
        {
            await _options.ForkFromEntryAsync(entryId, _cancellationToken);
            var snapshot = await _loadSessionSnapshotAsync(_cancellationToken);
            // RefreshAfterPossibleSessionChangeAsync (called via _rebind during ForkAsync) already
            // refreshes the session snapshot and requests a render; the runtime facade handles
            // resubscription on session rebind. We just apply the snapshot and show the
            // confirmation here.
            _appContext.Post(() =>
            {
                if (snapshot is not null) _applySessionSnapshot(snapshot, false);
                _gateway.Update(s => s.AppendSystem("Forked conversation from selected message."));
                _renderCoordinator.Render();
            });
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fork from entry {EntryId} failed", entryId);
            _appContext.Post(() =>
            {
                _gateway.Update(s => s.AppendSystem($"Fork failed: {ex.Message}", true));
                _renderCoordinator.Render();
            });
        }
    }

    internal void HandleSelectionCopied(string text, bool copied)
    {
        var message = copied
            ? $"Copied {text.Length} selected character{(text.Length == 1 ? string.Empty : "s")} to clipboard."
            : "Selected text, but the OS clipboard is unavailable.";
        _gateway.Update(s => s.AppendSystem(message, isError: !copied));
    }

    private static string MessageText(TuiTranscriptItem item)
    {
        if (!string.IsNullOrEmpty(item.Text)) return item.Text;
        if (item.ToolResult is not null) return item.ToolResult.ToString() ?? string.Empty;
        return string.Empty;
    }
}
