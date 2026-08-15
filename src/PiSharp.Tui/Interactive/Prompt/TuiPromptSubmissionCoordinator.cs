using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Harness;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Input;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.Shell;

namespace PiSharp.Tui.Interactive.Prompt;

internal sealed class TuiPromptSubmissionCoordinator
{
    private static readonly TimeSpan TransientSystemMessageLifetime = TimeSpan.FromSeconds(6);

    private readonly ILogger<TuiPromptSubmissionCoordinator> _logger;
    private readonly TuiShellView _shell;
    private readonly TuiInlineSelectionCoordinator _inlineSelection;
    private readonly TuiStateGateway _gateway;
    private readonly TuiSessionContext _session;
    private readonly TuiHostOptions _options;
    private readonly PromptFileReferenceCompletionProvider _fileReferenceCompletionProvider;
    private readonly IReadOnlySet<string> _extensionLoadCommandWhitelist;
    private readonly Func<TuiExtensionLoadStatus?> _getExtensionLoadStatus;

    internal TuiCommandController CommandController { get; set; } = null!;

    internal TuiPromptSubmissionCoordinator(
        TuiShellView shell,
        TuiInlineSelectionCoordinator inlineSelection,
        TuiStateGateway gateway,
        TuiSessionContext session,
        TuiHostOptions options,
        PromptFileReferenceCompletionProvider fileReferenceCompletionProvider,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<TuiPromptSubmissionCoordinator>() ?? NullLogger<TuiPromptSubmissionCoordinator>.Instance;
        _shell = shell;
        _inlineSelection = inlineSelection;
        _gateway = gateway;
        _session = session;
        _options = options;
        _fileReferenceCompletionProvider = fileReferenceCompletionProvider;
        _extensionLoadCommandWhitelist = options.ExtensionLoadCommandWhitelist ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _getExtensionLoadStatus = options.GetExtensionLoadStatus is null
            ? GetInactiveExtensionLoadStatus
            : () => options.GetExtensionLoadStatus();
    }

    private static TuiExtensionLoadStatus? GetInactiveExtensionLoadStatus()
        => null;

    internal IReadOnlyList<PromptCompletion> CompletePrompt(string text, int cursorOffset)
    {
        if (_inlineSelection.CurrentSession is not null)
            return _inlineSelection.CurrentSession.Complete(text).Select(value => new PromptCompletion(value, value, Prefix: text)).ToArray();
        if (text.StartsWith("/", StringComparison.Ordinal))
            return (_options.CompleteCommand?.Invoke(text) ?? []).Select(value => new PromptCompletion(value, value, Prefix: text, AppendSpace: true)).ToArray();
        return _fileReferenceCompletionProvider.Complete(text, cursorOffset);
    }

    internal async Task HandleSubmitAsync(string text, CancellationToken token)
    {
        _logger.LogDebug("TUI submit received textLength={TextLength} isCommand={IsCommand} commandInProgress={CommandInProgress} inlineSelectionActive={InlineSelectionActive}",
            text.Length, text.StartsWith("/", StringComparison.Ordinal), CommandController.IsCommandInProgress, _inlineSelection.CurrentSession is not null);

        try
        {
            if (_inlineSelection.CompleteInlineSelection(text))
            {
                _logger.LogDebug("TUI submit completed active inline selection");
                return;
            }
            if (IsBlockedByExtensionLoad(text))
            {
                _logger.LogDebug("TUI submit blocked while extensions are loading text={Text}", text);
                _gateway.Update(s => s.AppendSystem("Extensions are still loading. Please wait.",
                    expiresAfter: TimeSpan.FromSeconds(4)));
                return;
            }
            if (text.StartsWith("/", StringComparison.Ordinal) && await CommandController.TryHandleCommandAsync(text, token))
            {
                _logger.LogDebug("TUI submit handled as command text={Text}", text);
                return;
            }
            var input = _options.ProcessInputAsync is null
                ? new TuiInputHookResult(false, text, null)
                : await _options.ProcessInputAsync(text, null, "interactive", token).ConfigureAwait(false);
            if (input.Handled)
            {
                await _gateway.RunOnTuiAsync(() => _shell.Prompt.RecordSubmittedPrompt(text)).ConfigureAwait(false);
                return;
            }
            text = input.Text;
            if (await CommandController.TryHandleCommandAsync(text, token))
            {
                _logger.LogDebug("TUI submit handled as command text={Text}", text);
                return;
            }
            await _gateway.RunOnTuiAsync(() => _shell.Prompt.RecordSubmittedPrompt(text)).ConfigureAwait(false);
            var processed = _options.ProcessFileReferencesAsync is null
                ? (Text: text, Images: (IReadOnlyList<ImageContent>)[])
                : await _options.ProcessFileReferencesAsync(text, _options.WorkingDirectory ?? Environment.CurrentDirectory, token);
            var images = input.Images is { Count: > 0 } ? input.Images.Concat(processed.Images).ToArray() : processed.Images;

            if (_session.CurrentRuntime.Phase == AgentHarnessPhase.Idle)
            {
                _ = RunAgentTurnAsync(processed.Text, images, token);
            }
            else
            {
                var content = new List<MessageContent> { new TextContent(processed.Text) };
                if (images is { Count: > 0 }) content.AddRange(images);
                _session.CurrentRuntime.Steer(AgentMessages.User(content));
                await _gateway.RunOnTuiAsync(() =>
                {
                    _gateway.Update(s => s.AppendSystem($"Message queued ({s.PendingMessageCount + 1} pending).",
                        expiresAfter: TimeSpan.FromSeconds(6),
                        systemMessageTag: "pending-message") with
                    { PendingMessageCount = s.PendingMessageCount + 1 });
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _session.AbortPending = true;
            await _gateway.RunOnTuiAsync(() =>
                _gateway.Update(s => s.AppendSystem("Request aborted.",
                    systemMessageTag: "abort",
                    removeDelayAfterEvent: TimeSpan.FromSeconds(2),
                    expiresAfter: TimeSpan.FromSeconds(30)))
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt dispatch failed");
            await _gateway.RunOnTuiAsync(() =>
            {
                _shell.Prompt.SetPromptText(text);
                _shell.Prompt.FocusAtEnd();
                _gateway.Update(s => s.AppendSystem($"Error: {ex.Message}", true));
            }).ConfigureAwait(false);
        }
    }

    private async Task RunAgentTurnAsync(string text, IReadOnlyList<ImageContent> images, CancellationToken token)
    {
        try
        {
            await Task.Run(() => _session.CurrentRuntime.PromptAsync(text, images, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _session.AbortPending = true;
            await _gateway.RunOnTuiAsync(() =>
                _gateway.Update(s => s.AppendSystem("Request aborted.",
                    systemMessageTag: "abort",
                    removeDelayAfterEvent: TimeSpan.FromSeconds(2),
                    expiresAfter: TimeSpan.FromSeconds(30)))
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent turn failed");
            await _gateway.RunOnTuiAsync(() =>
                _gateway.Update(s => s.AppendSystem($"Error: {ex.Message}", true))
            ).ConfigureAwait(false);
        }
    }

    private bool IsBlockedByExtensionLoad(string text)
    {
        var extensionLoadStatus = _getExtensionLoadStatus();
        if (extensionLoadStatus is not { BlocksInput: true }) return false;
        if (_extensionLoadCommandWhitelist.Count == 0) return true;
        if (!text.StartsWith("/", StringComparison.Ordinal)) return true;
        var command = text.Trim().Split([' '], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (command is null) return true;
        return !_extensionLoadCommandWhitelist.Contains(command);
    }
}
