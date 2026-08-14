using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Shell;

internal sealed class TuiRenderCoordinator : IDisposable
{
    private const int InlineSelectionVisibleItems = 5;
    private static readonly TimeSpan TransientSystemMessageLifetime = TimeSpan.FromSeconds(6);
    private readonly TuiShellView _shell;
    private readonly Func<TuiRenderState> _getState;
    private readonly Action<TuiRenderState> _setState;
    private readonly ITuiApplicationContext _appContext;
    private readonly TuiRenderScheduler _renderScheduler;
    private readonly Func<TuiFooterSnapshot> _createFooterSnapshot;
    private readonly TuiProfilingCounters? _profilingCounters;
    private readonly Func<bool> _getHeaderExpanded;
    private Func<InlineSelectionSession?>? _getSelectionSession;
    private readonly Func<TuiExtensionLoadStatus?> _getExtensionLoadStatus;
    private readonly Func<IReadOnlyList<string>> _getActiveTools;
    private readonly ILogger<TuiRenderCoordinator> _logger;
    private readonly CancellationToken _cancellationToken;

    private readonly bool _hasExtensionLoadStatus;
    private TuiExtensionLoadStatus? _extensionLoadStatus;
    private bool _extensionLoadWasActive;
    private bool _disposed;
    private int _workingFrameIndex;
    private object? _workingAnimationToken;
    private object? _extensionLoadPollingToken;
    private object? _transientSystemMessageCleanupToken;
    private IReadOnlyList<TuiMenuEntry>? _lastRenderedMenus;
    private DateTimeOffset _lastModifiedFilesRefresh = DateTimeOffset.MinValue;
    private static readonly TimeSpan ModifiedFilesRefreshInterval = TimeSpan.FromSeconds(2);
    private readonly Action<string> _invokeCommand;

    public TuiRenderCoordinator(
        TuiShellView shell,
        Func<TuiRenderState> getState,
        Action<TuiRenderState> setState,
        ITuiApplicationContext appContext,
        Func<TuiFooterSnapshot> createFooterSnapshot,
        TuiProfilingCounters? profilingCounters = null,
        TimeSpan? renderFrameInterval = null,
        Func<InlineSelectionSession?>? getSelectionSession = null,
        Func<bool>? getHeaderExpanded = null,
        Func<TuiExtensionLoadStatus?>? getExtensionLoadStatus = null,
        Func<IReadOnlyList<string>>? getActiveTools = null,
        Action<string>? invokeCommand = null,
        CancellationToken cancellationToken = default,
        ILoggerFactory? loggerFactory = null)
    {
        _shell = shell;
        _getState = getState;
        _setState = setState;
        _appContext = appContext;
        _createFooterSnapshot = createFooterSnapshot;
        _profilingCounters = profilingCounters;
        _getSelectionSession = getSelectionSession ?? (() => null);
        _getHeaderExpanded = getHeaderExpanded ?? (() => false);
        _hasExtensionLoadStatus = getExtensionLoadStatus is not null;
        _getExtensionLoadStatus = getExtensionLoadStatus ?? (() => null);
        _getActiveTools = getActiveTools ?? (() => Array.Empty<string>());
        _invokeCommand = invokeCommand ?? (_ => { });
        _cancellationToken = cancellationToken;
        _logger = loggerFactory?.CreateLogger<TuiRenderCoordinator>() ?? NullLogger<TuiRenderCoordinator>.Instance;
        _renderScheduler = new TuiRenderScheduler(appContext, renderFrameInterval);

        var initialLoadStatus = _getExtensionLoadStatus();
        _extensionLoadStatus = initialLoadStatus;
        _extensionLoadWasActive = initialLoadStatus?.IsLoading ?? false;

        appContext.SizeChanging += HandleApplicationResize;
    }

    public void Initialize()
    {
        if (_hasExtensionLoadStatus)
        {
            var extensionLoadStatus = _getExtensionLoadStatus();
            if (extensionLoadStatus is { IsLoading: true })
            {
                var state = _getState().AppendSystem(FormatExtensionLoadStartedMessage(extensionLoadStatus), pinToTop: true, expiresAfter: TransientSystemMessageLifetime);
                _setState(state);
            }

            _extensionLoadPollingToken = _appContext.AddTimeout(TimeSpan.FromMilliseconds(200), PollExtensionLoadStatus);
        }

        _transientSystemMessageCleanupToken = _appContext.AddTimeout(TimeSpan.FromSeconds(1), CleanupExpiredSystemRows);
    }

    public void RequestRender(CancellationToken token = default) => _renderScheduler.RequestRender(Render, token);

    public Func<InlineSelectionSession?>? SelectionSessionGetter { set => _getSelectionSession = value ?? (() => null); }
    public Func<bool>? IsInputCaptured { get; set; }

    public void Render()
    {
        _profilingCounters?.Increment(TuiProfilingCounterNames.RenderCycle);

        var state = _getState();
        var prompt = _shell.Prompt;
        var selectionSession = _getSelectionSession();

        var suggestionsVisible = prompt.Suggestions.Count > 0;

        var editorComponent = state.BridgeSlots.LastOrDefault(slot => slot.Placement == "editor" && slot.Visible);
        _shell.SetEditorComponentSlot(editorComponent);
        _shell.PromptTitle.Text = selectionSession is null ? "─ Message" : $"─ {selectionSession.Title}";

        _shell.Header.Render(state, _getHeaderExpanded(), _extensionLoadStatus);
        var footerSnapshot = _createFooterSnapshot();
        _shell.Footer.Render(state, footerSnapshot, _getActiveTools());
        _shell.WorkingIndicator.Render(state, _workingFrameIndex);

        _shell.LeftSidebar.Render(state);
        _shell.RightSidebar.Render(state);
        _shell.SetSidebarVisibility(state.LeftSidebarVisible, state.RightSidebarVisible);

        if (!ReferenceEquals(_lastRenderedMenus, state.CustomMenus))
        {
            _shell.MenuBar.Menus = TuiMenuBarBuilder.Build(state.CustomMenus, _invokeCommand);
            _lastRenderedMenus = state.CustomMenus;
        }
        var inlineSelectionActive = selectionSession is not null;
        _shell.Suggestions.SetCompletions(
            prompt.Completions,
            prompt.SelectedSuggestionIndex,
            TuiShellView.InlineSuggestionHeight,
            maxVisibleItems: inlineSelectionActive ? InlineSelectionVisibleItems : null,
            singleLineItems: inlineSelectionActive);
        _shell.Suggestions.Visible = suggestionsVisible;

        var headerHeight = TuiLayoutMetrics.TextLineCount(_shell.Header);
        var footerHeight = TuiLayoutMetrics.TextLineCount(_shell.Footer);
        var suggestionsHeight = suggestionsVisible ? Math.Min(TuiShellView.InlineSuggestionHeight, TuiLayoutMetrics.TextLineCount(_shell.Suggestions)) : 0;
        var workingIndicatorHeight = _shell.WorkingIndicator.Visible ? TuiLayoutMetrics.TextLineCount(_shell.WorkingIndicator) : 0;
        var promptHeight = TuiLayoutMetrics.PromptContentHeight(prompt);

        var metrics = TuiShellLayout.CalculateMetrics(
            headerHeight, footerHeight, suggestionsHeight,
            workingIndicatorHeight, suggestionsVisible,
            _shell.WorkingIndicator.Visible, promptHeight);
        _logger.LogDebug(
            "Footer render requested model={ModelDisplay} thinking={ThinkingLevel} branch={GitBranch} totalTokens={TotalTokens} contextPercent={ContextPercent} contextKnown={ContextKnown} footerHeight={FooterHeight} suggestionsVisible={SuggestionsVisible}",
            state.ModelDisplay,
            state.ThinkingLevel,
            footerSnapshot.GitBranch,
            footerSnapshot.TotalTokens,
            footerSnapshot.ContextPercent,
            footerSnapshot.ContextPercentKnown,
            footerHeight,
            suggestionsVisible);
        _shell.ApplyLayout(metrics);

        state = TuiPendingEditorText.Apply(state, prompt);
        state = state.RemoveExpiredSystemRows(DateTimeOffset.UtcNow);
        _setState(state);

        var now = DateTimeOffset.UtcNow;
        if (now - _lastModifiedFilesRefresh > ModifiedFilesRefreshInterval)
        {
            _lastModifiedFilesRefresh = now;
            _ = RefreshModifiedFilesAsync(_cancellationToken);
        }

        _shell.Chat.Render(state);
        _shell.ChatScrollBar.ScrollableContentSize = _shell.Chat.ScrollableRowCount;
        _shell.ChatScrollBar.VisibleContentSize = _shell.Chat.VisibleRowCount;
        _shell.ChatScrollBar.Position = _shell.Chat.ScrollTop;
        EnsureWorkingAnimation();
        if (editorComponent is not null)
        {
            if (Application.Top == _shell.Window && !_shell.EditorComponent.HasFocus && IsInputCaptured?.Invoke() != true)
            {
                _shell.EditorComponent.SetFocus();
                _shell.EditorComponent.MoveEnd();
            }
        }
        else if (Application.Top == _shell.Window && !prompt.HasFocus && IsInputCaptured?.Invoke() != true)
        {
            prompt.FocusAtEnd();
        }
    }


    private async Task RefreshModifiedFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var files = await ModifiedFilesProvider.GetModifiedFilesAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
            await _appContext.InvokeAsync(() => _shell.LeftSidebar.SetModifiedFiles(files)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation requested during shutdown
        }
        catch
        {
            // Silently ignore git status failures (non-repo, git not installed, etc.)
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_workingAnimationToken is not null) _appContext.RemoveTimeout(_workingAnimationToken);
        if (_extensionLoadPollingToken is not null) _appContext.RemoveTimeout(_extensionLoadPollingToken);
        if (_transientSystemMessageCleanupToken is not null) _appContext.RemoveTimeout(_transientSystemMessageCleanupToken);
        _appContext.SizeChanging -= HandleApplicationResize;
    }

    private void HandleApplicationResize(object? _, SizeChangedEventArgs args) => RequestRender();

    private bool PollExtensionLoadStatus()
    {
        UpdateExtensionLoadStatusFromRuntime();
        return true;
    }

    private bool CleanupExpiredSystemRows()
    {
        var before = _getState().Transcript.Count;
        var state = _getState().RemoveExpiredSystemRows(DateTimeOffset.UtcNow);
        _setState(state);
        if (state.Transcript.Count != before) RequestRender();
        return true;
    }

    private void UpdateExtensionLoadStatusFromRuntime()
    {
        var latest = _getExtensionLoadStatus();
        var previous = _extensionLoadStatus;
        if (Equals(previous, latest)) return;

        if (latest is null)
        {
            _extensionLoadWasActive = false;
            _extensionLoadStatus = null;
            RequestRender();
            return;
        }

        _extensionLoadStatus = latest;
        var isActive = latest.IsLoading;
        if (!_extensionLoadWasActive && isActive)
        {
            var state = _getState().AppendSystem(FormatExtensionLoadStartedMessage(latest), pinToTop: true, expiresAfter: TransientSystemMessageLifetime);
            _setState(state);
        }
        else if (_extensionLoadWasActive && !isActive)
        {
            var state = _getState().AppendSystem(FormatExtensionLoadCompletedMessage(latest), isError: latest.Failed > 0, pinToTop: true, expiresAfter: latest.Failed > 0 ? null : TransientSystemMessageLifetime);
            _setState(state);
        }

        _extensionLoadWasActive = isActive;
        RequestRender();
    }

    private bool ShouldAnimateWorkingIndicator()
    {
        var state = _getState();
        return state.IsBusy && state.WorkingVisible && (state.WorkingIndicator?.Visible ?? true) && state.WorkingIndicator?.Spinner is null;
    }

    private void EnsureWorkingAnimation()
    {
        if (!ShouldAnimateWorkingIndicator())
        {
            if (_workingAnimationToken is not null)
            {
                _appContext.RemoveTimeout(_workingAnimationToken);
                _workingAnimationToken = null;
            }
            return;
        }

        if (_workingAnimationToken is not null) return;
        _workingAnimationToken = _appContext.AddTimeout(TimeSpan.FromMilliseconds(80), () =>
        {
            if (!ShouldAnimateWorkingIndicator())
            {
                _workingAnimationToken = null;
                RequestRender();
                return false;
            }

            _workingFrameIndex++;
            RequestRender();
            return true;
        });
    }

    private static string FormatExtensionLoadStartedMessage(TuiExtensionLoadStatus status)
        => $"Extensions loading {status.Ready}/{status.Total} (active {status.Active})";

    private static string FormatExtensionLoadCompletedMessage(TuiExtensionLoadStatus status)
        => status.FormatCompletedMessage();
}
