using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Extensions;
using PiSharp.Logging;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Harness;
using PiSharp.Tui.Interactive.Input;
using PiSharp.Tui.Interactive.Keybindings;
using PiSharp.Tui.Interactive.Prompt;
using PiSharp.Tui.Interactive.Rendering;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.Shell;
using PiSharp.Tui.Interactive.Theme;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class TuiHost(TuiHostOptions options)
{
    private ILogger<TuiHost> _logger = options.LoggerFactory?.CreateLogger<TuiHost>() ?? NullLogger<TuiHost>.Instance;
    private TimeSpan TransientSystemMessageLifetime => options.TransientSystemMessageLifetime ?? TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PostStartupMessageLifetime = TimeSpan.FromSeconds(30);

    internal static void HandleConsoleCancelKeyPress(
        ITerminalCancelKeyPressEvent args,
        ITuiApplicationContext appContext,
        Action handleCtrlCShortcut)
    {
        args.Cancel = true;
        appContext.Post(handleCtrlCShortcut);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        // A bare TuiHost (tests, embedding) gets a real file logger instead of a silent
        // NullLogger: the fallback factory writes to the shared PiSharp log directory.
        var loggerFactory = options.LoggerFactory;
        if (loggerFactory is null)
        {
            loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                CliFileLogging.AddConfiguredFileLogging(builder, Directory.GetCurrentDirectory());
            });
            _logger = loggerFactory.CreateLogger<TuiHost>();
        }

        ConsoleTerminalSessionLifetimeEvents.LoggerFactory = loggerFactory;
        var terminalScreenSession = options.TerminalScreenSession ?? AnsiTerminalScreenSession.CreateDefault(loggerFactory);
        terminalScreenSession.Enter();
        var driver = options.ConsoleDriver;
        var driverName = driver is null ? TuiConsoleDriverName.DefaultForCurrentPlatform() : "FakeDriver";
        if (driver is null) TuiConsoleDriverName.PrepareConsoleForDriver(driverName);
        try
        {
            _logger.LogDebug("TUI driver initializing driver={DriverName}", driverName);
            Application.Init(driver!, driverName);
            terminalScreenSession.RestoreBracketedPaste();
            // F6/Shift+F6 are Terminal.Gui's built-in NextTabGroup/PrevTabGroup keys.
            // In the real terminal these commands mark the key as Handled and shift focus
            // away from the prompt, silently blocking our sidebar toggle shortcuts on
            // subsequent presses. We own the full keyboard contract for this TUI, so
            // removing these built-in bindings is safe.
            Application.KeyBindings.Remove(Key.F6);
            Application.KeyBindings.Remove(Key.F6.WithShift);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TUI driver initialization failed driver={DriverName}", driverName);
            terminalScreenSession.Exit();
            throw;
        }
        TuiTheme.Apply(options.Theme);
        TuiShortcutRegistrar.LoggerFactory = loggerFactory;

        var keybindingsStore = new TuiKeybindingStore(options.KeybindingsDefaults ?? TuiBuiltInShortcutCatalog.Bindings);
        TuiShortcutRegistrar.DefaultStore = keybindingsStore;
        var initialKeybindingsDiagnostics = new List<string>();
        if (!string.IsNullOrEmpty(options.KeybindingsPath) && File.Exists(options.KeybindingsPath))
        {
            _logger.LogDebug("TUI keybindings load started path={Path}", options.KeybindingsPath);
            if (KeybindingsLoader.TryReadFile(options.KeybindingsPath, out var keybindingsJson, out var keybindingsError))
            {
                keybindingsStore.Reload(keybindingsJson);
                _logger.LogDebug("TUI keybindings loaded path={Path} bindings={BindingCount} diagnostics={DiagnosticCount}",
                    options.KeybindingsPath, keybindingsStore.CommandDescriptors.Count, keybindingsStore.Diagnostics.Count);
                initialKeybindingsDiagnostics.AddRange(keybindingsStore.Diagnostics);
            }
            else if (keybindingsError is not null)
            {
                initialKeybindingsDiagnostics.Add(keybindingsError);
                _logger.LogWarning("TUI keybindings load failed path={Path} error={Error}", options.KeybindingsPath, keybindingsError);
            }
        }

        var appContext = options.ApplicationContext ?? new TerminalGuiApplicationContext();

        // Owns the deferred startup hydration: cancelling it during teardown stops any
        // in-flight metadata/daemon work from mutating the UI once the loop is shutting down.
        using var hostLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var runtime = options.Runtime;
        var startupAsync = options.StartupAsync;

        // Local callers (no startup hook) keep the host's original synchronous hydration:
        // session metadata and startup messages are resolved before the shell is built, so
        // the first frame already reflects them and no late mutation can race the app loop.
        // Remote callers (StartupAsync) defer this work until after the first render so the
        // shell paints before any daemon round-trip can block it.
        TuiRenderState state;
        if (startupAsync is not null)
        {
            state = TuiRenderState.Empty(
                options.SessionId, options.SessionFile,
                runtime.Model, runtime.ThinkingLevel,
                sessionName: null);
        }
        else
        {
            var sessionName = await options.GetSessionNameAsync(cancellationToken);
            state = TuiRenderState.Empty(
                options.SessionId, options.SessionFile,
                runtime.Model, runtime.ThinkingLevel,
                sessionName);
            if (options.GetSessionSnapshotAsync is not null)
            {
                var snapshot = await options.GetSessionSnapshotAsync(cancellationToken);
                state = state.HydrateSession(snapshot.SessionId, snapshot.SessionFile, snapshot.SessionName, snapshot.BranchEntries);
            }
            foreach (var message in options.StartupMessages ?? [])
                state = state.AppendSystem(message, pinToTop: true, expiresAfter: TransientSystemMessageLifetime);
        }
        foreach (var diagnostic in initialKeybindingsDiagnostics)
            state = state.AppendSystem(diagnostic, isError: true);
        _logger.LogInformation("TUI host starting sessionId={SessionId} model={Model} thinking={Thinking} sessionName={SessionName}",
            state.SessionId, state.ModelDisplay, state.ThinkingLevel, state.SessionName);

        var stateStore = new RenderStateStore(state);

        var shell = new TuiShellView(loggerFactory);
        shell.ProfilingCounters = options.ProfilingCounters;
        var chatPipeline = new ChatRowRenderPipeline(options.GetExtensionRegistry?.Invoke(), loggerFactory: loggerFactory);
        shell.Chat.TranscriptItemRenderer = (item, renderState, width) =>
        {
            options.ProfilingCounters?.Increment(TuiProfilingCounterNames.TranscriptItemRender);
            return chatPipeline.Render(item, renderState, width);
        };
        shell.Chat.ProfilingCounters = options.ProfilingCounters;

        async Task<TuiSessionSnapshot?> LoadSessionSnapshotAsync(CancellationToken token)
            => options.GetSessionSnapshotAsync is null ? null : await options.GetSessionSnapshotAsync(token);

        void ApplySessionSnapshot(TuiSessionSnapshot snapshot, bool preserve = false)
            => stateStore.Update(s => TuiSessionSwitch.ApplySnapshot(s, snapshot, preserve));

        var headerExpanded = false;
        var footerSnapshotProvider = new TuiFooterSnapshotProvider(loggerFactory: loggerFactory);
        var timingOptions = options.TimingOptions ?? TuiTimingOptions.Default;

        TuiShortcutActionHandler? shortcutActionsRef = null;
        void InvokeMenuCommand(string command)
        {
            if (shortcutActionsRef is not null)
                shortcutActionsRef.DispatchShortcutCommand(command);
        }
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => stateStore.Snapshot(), s => stateStore.Replace(s),
            appContext,
            () =>
            {
                var current = stateStore.Snapshot();
                return options.FooterSnapshot?.Invoke(current) ?? footerSnapshotProvider.CreateSnapshot(current, options.WorkingDirectory ?? Environment.CurrentDirectory);
            },
            options.ProfilingCounters,
            renderFrameInterval: timingOptions.RenderFrameInterval,
            getHeaderExpanded: () => headerExpanded,
            getExtensionLoadStatus: () => options.GetExtensionLoadStatus?.Invoke(),
            getActiveTools: () => runtime.ActiveToolNames,
            invokeCommand: InvokeMenuCommand,
            cancellationToken: cancellationToken,
            loggerFactory: loggerFactory,
            updateState: stateStore.Update);

        var inlineSelection = new TuiInlineSelectionCoordinator(shell.Prompt, () => renderCoordinator.RequestRender(), appContext.Post);
        renderCoordinator.SelectionSessionGetter = () => inlineSelection.CurrentSession;

        var bridge = new ExtensionUiBridgeHost(shell.Window,
            updateState: u => { stateStore.Update(u); renderCoordinator.RequestRender(); },
            getEditorText: () => shell.Prompt.PromptText,
            setEditorText: t => shell.Prompt.SetPromptText(t),
            getState: () => stateStore.Snapshot(),
            loggerFactory: loggerFactory)
        {
            DispatchUi = appContext.Post,
            RestoreFocus = () => shell.Prompt.FocusAtEnd(),
            ShowNotification = message =>
            {
                stateStore.Update(s => s.AppendSystem(message, expiresAfter: TransientSystemMessageLifetime));
                renderCoordinator.RequestRender();
            }
        };
        renderCoordinator.IsInputCaptured = () => bridge.HasActiveCustomUi;
        options.ConfigureUiBridge?.Invoke(bridge);
        var extensionUi = new TuiExtensionUi(bridge, SelectInlineWithLoggingAsync);

        var shortcutController = new TuiShortcutController(new TuiShortcutControllerOptions(
            () => options.GetExtensionShortcuts?.Invoke() ?? [], extensionUi,
            message => loggerFactory.CreateLogger<TuiShortcutController>().LogWarning("{Message}", message)));
        // Extension shortcuts come from a potentially-remote source, so they are read only on a
        // background thread (see TuiShortcutController) and never on the UI key path. Kick off the
        // first refresh here; invalidations below clear the cache and schedule another refresh.
        _ = shortcutController.RefreshExtensionShortcutsAsync(hostLifetime.Token);
        renderCoordinator.OnExtensionShortcutsChanged = () =>
        {
            shortcutController.InvalidateExtensionShortcuts();
            _ = shortcutController.RefreshExtensionShortcutsAsync(hostLifetime.Token);
        };

        // sessionRefresh / onAbortRequested are set after construction to break circular dependency with commandController.
        Func<CancellationToken, Task>? sessionRefresh = null;
        Action? onAbortRequested = null;

        async Task<string?> SelectInlineWithLoggingAsync(string title, IReadOnlyList<string> choices, CancellationToken token)
        {
            _logger.LogDebug("TUI inline selection requested title={Title} choices={ChoiceCount}", title, choices.Count);
            var selected = await inlineSelection.SelectInlineAsync(title, choices, token).ConfigureAwait(false);
            _logger.LogDebug("TUI inline selection completed title={Title} selected={HasSelection}", title, !string.IsNullOrWhiteSpace(selected));
            return selected;
        }

        var commandController = new TuiCommandController(new TuiCommandControllerOptions(
            () => stateStore.Snapshot(),
            next => { stateStore.Replace(next); renderCoordinator.RequestRender(); },
            () => runtime.Abort(),
            () => appContext.Post(() => appContext.RequestStop(shell.Window)),
            () => TuiHotkeyText.RenderFromBindings(keybindingsStore.CommandDescriptors, shortcutController.BuildExtensionShortcutBindings()),
            commandText => new TuiCommandDispatchRequest(commandText, SelectInlineWithLoggingAsync,
                (string text, CancellationToken ct) => PromptDialog.InputAsync(text, ct, dispatcher: appContext),
                (msg, isErr, _) =>
                {
                    stateStore.Update(s => s.AppendSystem(msg, isErr, expiresAfter: isErr ? null : TransientSystemMessageLifetime));
                    renderCoordinator.RequestRender();
                    return Task.CompletedTask;
                },
                (a, b, c, d) => SessionSelectorDialog.SelectAsync(a, b, c, d, appContext: appContext)),
            options.DispatchCommandAsync,
            token => sessionRefresh?.Invoke(token) ?? Task.CompletedTask,
            () => runtime.Phase,
            OnAbortRequested: () => onAbortRequested?.Invoke(),
            UpdateState: update => { var next = stateStore.Update(update); renderCoordinator.RequestRender(); return next; }),
            loggerFactory);
        // Wire interactive UI requests (select/input/confirm from daemon slash
        // commands or server extension UI) to the local dialog pipeline. With no
        // handlers the bridge keeps its canned non-interactive defaults.
        bridge.SelectAction = SelectInlineWithLoggingAsync;
        bridge.InputAction = (string prompt, string? initialValue, CancellationToken ct)
            => PromptDialog.InputAsync(prompt, initialValue, ct, dispatcher: appContext);
        bridge.ConfirmAction = (string title, string? message, CancellationToken ct)
            => ConfirmDialog.ConfirmAsync(title, message, ct, dispatcher: appContext);
        bridge.ApprovalAction = async (string title, string? message, CancellationToken ct)
            => await ConfirmDialog.ConfirmAsync(title, message, ct, dispatcher: appContext) ? "allow" : "deny";

        var sessionContext = new TuiSessionContext { CurrentRuntime = runtime, HeaderExpanded = headerExpanded };
        var stateGateway = new TuiStateGateway(() => stateStore.Snapshot(), s => stateStore.Replace(s), renderCoordinator, appContext, cancellationToken, updateState: stateStore.Update);
        var harnessLifecycle = new TuiHarnessLifecycleCoordinator(
            sessionContext, options, appContext, renderCoordinator,
            stateStore, LoadSessionSnapshotAsync, ApplySessionSnapshot, loggerFactory);
        var shortcutActions = new TuiShortcutActionHandler(
            shell, inlineSelection, appContext, stateGateway, sessionContext, options, cancellationToken, loggerFactory);
        var transcriptController = new TuiTranscriptInteractionController(
            shell, options, appContext, renderCoordinator, stateGateway, sessionContext,
            LoadSessionSnapshotAsync, ApplySessionSnapshot, cancellationToken, loggerFactory);
        var submissionCoordinator = new TuiPromptSubmissionCoordinator(
            shell, inlineSelection, stateGateway, sessionContext, options,
            new PromptFileReferenceCompletionProvider(
                options.WorkingDirectory ?? Environment.CurrentDirectory,
                gitVisibility: GitVisibilityService.TryCreate(Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory), loggerFactory.CreateLogger(nameof(GitVisibilityService))),
                loggerFactory: loggerFactory,
                profilingCounters: options.ProfilingCounters),
            loggerFactory);
        submissionCoordinator.CommandController = commandController;
        shortcutActions.CommandController = commandController;
        shortcutActionsRef = shortcutActions;

        sessionRefresh = harnessLifecycle.RefreshAfterPossibleSessionChangeAsync;
        onAbortRequested = () => sessionContext.AbortPending = true;
        options.Runtime.OnHarnessReplaced = harnessLifecycle.RefreshAfterPossibleSessionChangeAsync;

        var shortcutDispatcher = TuiShortcutDispatcher.CreateDefaultAppDispatcher();
        var shortcutContext = new TuiShortcutContext(
            shortcutActions.HandleAbortShortcut,
            () => appContext.Post(() => appContext.RequestStop(shell.Window)),
            shortcutActions.HandleClearEditorShortcut,
            () => shortcutActions.DispatchShortcutCommand("/model"),
            () => shortcutActions.DispatchShortcutCommand("/tree"),
            () => shortcutActions.UpdateState(s => s.ToggleToolOutput()),
            () => shortcutActions.UpdateState(s => s.ToggleThinking()),
            () => shortcutActions.HandleCycleThinkingLevelShortcut(),
            () =>
            {
                sessionContext.HeaderExpanded = !sessionContext.HeaderExpanded;
                renderCoordinator.RequestRender();
            },
            shortcutActions.HandleCtrlCShortcut)
        {
            ToggleLeftSidebar = shortcutActions.HandleToggleLeftSidebar,
            ToggleRightSidebar = shortcutActions.HandleToggleRightSidebar
        };

        using var inputCoordinator = new TuiInputCoordinator(
            shell.Window, shell.Chat, shell.Prompt,
            () => renderCoordinator.RequestRender(),
            shortcutDispatcher, shortcutContext, shortcutController,
            shortcutActions.ReportShortcutError, cancellationToken, () => bridge.HasActiveCustomUi, () => shell.HasActiveEditorComponent, loggerFactory);
        inputCoordinator.Attach();

        var customUiCapture = new ExtensionCustomUiInputCapture(bridge);
        using var inputRouter = new TuiInputRouter(
            appContext,
            getActiveCapture: () => bridge.HasActiveCustomUi ? customUiCapture : null,
            tryHandleHostInput: inputCoordinator.TryHandleHostInput,
            tryDispatchShortcut: key =>
            {
                if (Application.Top != shell.Window) return false;
                return TuiShortcutRegistrar.TryDispatchShortcutKey(
                    key,
                    shortcutDispatcher,
                    shortcutContext,
                    shortcutController.BuildExtensionShortcutBindings,
                    shortcutActions.ReportShortcutError,
                    cancellationToken);
            },
            loggerFactory: loggerFactory);
        inputRouter.Attach();

        keybindingsStore.Changed += () => appContext.Post(() => renderCoordinator.RequestRender());
        using var keybindingsWatcher = string.IsNullOrEmpty(options.KeybindingsPath)
            ? null
            : new KeybindingsWatcher(options.KeybindingsPath, keybindingsStore, appContext.Post, shortcutActions.ReportShortcutError, loggerFactory.CreateLogger<KeybindingsWatcher>());

        ConsoleCancelEventHandler consoleCancelKeyPressHandler = (_, args) =>
        {
            _logger.LogDebug("Routing Console.CancelKeyPress through TUI Ctrl+C shortcut fallback");
            HandleConsoleCancelKeyPress(new TerminalCancelKeyPressEvent(args), appContext, shortcutActions.HandleCtrlCShortcut);
        };
        Console.CancelKeyPress += consoleCancelKeyPressHandler;

        shell.Chat.InteractionRequested += transcriptController.HandleChatInteraction;
        shell.Chat.ContextMenuRequested += transcriptController.HandleChatContextMenu;
        shell.Chat.SelectionCopied += transcriptController.HandleSelectionCopied;
        shell.Prompt.PostSuggestionUpdate = appContext.Post;
        shell.Prompt.CompleteAsync = (text, cursorOffset, cancellationToken) => Task.Run(
            () =>
            {
                options.ProfilingCounters?.Increment(TuiProfilingCounterNames.CompletionInvocation);
                return submissionCoordinator.CompletePrompt(text, cursorOffset);
            },
            cancellationToken);
        shell.Prompt.TranscriptScrollRequested += d =>
        {
            shell.Chat.ScrollLine(d);
            shell.Prompt.FocusAtEnd();
            renderCoordinator.RequestRender();
        };
        shell.Prompt.Submitted += submissionCoordinator.HandleSubmitAsync;

        using var suggestionSubscription = TuiRenderRequestRouter.ConnectPromptSuggestions(shell.Prompt, () => renderCoordinator.RequestRender());

        harnessLifecycle.BindInitialHarnessSubscription();
        renderCoordinator.Initialize();
        shell.Prompt.FocusAtEnd();

        if (startupAsync is not null)
        {
            // The remote startup handshake and session metadata hydrate only after the
            // initial frame is queued, so the shell renders before any daemon work can
            // block it. All state, theme, and prompt mutations are marshaled through the
            // app loop. The app loop owns the launch: the posted callback paints the
            // connecting state and starts hydration off-loop so the shell keeps
            // rendering, the host-lifetime token (cancelled during teardown) bounds any
            // further UI mutation, and the fault continuation observes anything the
            // hydration itself does not report. One delivered post is sufficient to
            // render the connecting view and launch hydration.
            appContext.Post(() =>
            {
                stateStore.Update(s => s.AppendSystem("Connecting to daemon…", pinToTop: true, expiresAfter: TransientSystemMessageLifetime));
                shell.Prompt.Enabled = false;
                try
                {
                    var hydration = Task.Run(() => HydrateStartupAsync(hostLifetime.Token), hostLifetime.Token);
                    _ = hydration.ContinueWith(
                        static (task, state) => ((ILogger<TuiHost>)state!).LogError(
                            task.Exception!.GetBaseException(), "TUI startup hydration failed unexpectedly"),
                        _logger,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch (OperationCanceledException) when (hostLifetime.IsCancellationRequested)
                {
                    _logger.LogDebug("TUI startup hydration was cancelled before it could start");
                }
            });
        }

        renderCoordinator.RequestRender(cancellationToken);

        if (options.PostStartupChecksAsync is not null)
        {
            Task InjectMessage(string message) =>
                appContext.InvokeAsync(() =>
                {
                    stateStore.Update(s => s.AppendSystem(message, pinToTop: false, expiresAfter: PostStartupMessageLifetime));
                    renderCoordinator.RequestRender();
                }, cancellationToken);
            _ = Task.Run(() => options.PostStartupChecksAsync(InjectMessage, cancellationToken), cancellationToken);
        }

        async Task HydrateStartupAsync(CancellationToken token)
        {
            try
            {
                _logger.LogDebug("TUI startup hydration started");
                // Only deferred-startup callers reach this function; the guard also keeps
                // the nullable flow analysis sound.
                if (startupAsync is null)
                    return;
                var startupResult = await startupAsync(token).ConfigureAwait(false);
                _logger.LogDebug("TUI startup phase completed");
                if (startupResult.Theme is not null)
                    await appContext.InvokeAsync(() => TuiTheme.Apply(startupResult.Theme), token).ConfigureAwait(false);

                var sessionName = await options.GetSessionNameAsync(token).ConfigureAwait(false);
                _logger.LogDebug("TUI session name resolved");
                TuiSessionSnapshot? snapshot = null;
                if (options.GetSessionSnapshotAsync is not null)
                    snapshot = await options.GetSessionSnapshotAsync(token).ConfigureAwait(false);
                _logger.LogDebug("TUI session snapshot resolved");

                _logger.LogDebug("TUI startup state apply requested");
                await appContext.InvokeAsync(() =>
                {
                    _logger.LogDebug("TUI startup state apply started");
                    if (snapshot is not null)
                    {
                        ApplySessionSnapshot(snapshot);
                    }
                    else
                    {
                        stateStore.Update(s => s with { SessionName = sessionName });
                    }
                    _logger.LogDebug(
                        "TUI startup state applied; placeholder session={PlaceholderSession}",
                        string.Equals(stateStore.Snapshot().SessionId, "connecting", StringComparison.Ordinal));
                    foreach (var message in options.StartupMessages ?? [])
                        stateStore.Update(s => s.AppendSystem(message, pinToTop: true, expiresAfter: TransientSystemMessageLifetime));
                    foreach (var message in startupResult.StartupMessages ?? [])
                        stateStore.Update(s => s.AppendSystem(message, pinToTop: true, expiresAfter: TransientSystemMessageLifetime));
                    shell.Prompt.Enabled = true;
                    renderCoordinator.RequestRender();
                    _logger.LogDebug("TUI startup hydration completed");
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _logger.LogDebug("TUI startup hydration cancelled; host is shutting down");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "TUI startup hydration failed; failed to connect to the daemon sessionId={SessionId}", options.SessionId);
                var errorMessage = $"Failed to connect to the daemon: {exception.Message}. Verify the daemon is running and restart the interactive session.";
                try
                {
                    // The persistent error is surfaced even during teardown; a shutdown
                    // marshal failure is caught below and logged instead of crashing.
                    await appContext.InvokeAsync(() =>
                    {
                        stateStore.Update(s => s.AppendSystem(errorMessage, isError: true, expiresAfter: null));
                        renderCoordinator.RequestRender();
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception marshalException) when (marshalException is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
                {
                    _logger.LogDebug(marshalException, "TUI startup error could not be surfaced; host is shutting down");
                }
            }
        }

        try
        {
            if (options.BeforeRunAsync is not null)
            {
                var runContext = new TuiHostRunContext(
                    shell.Window,
                    shell.Header,
                    shell.Chat,
                    shell.Prompt,
                    shell.Footer,
                    Application.Driver,
                    () => stateStore.Snapshot(),
                    () => renderCoordinator.RequestRender(),
                    InvokeMenuCommand);

                await options.BeforeRunAsync(runContext, cancellationToken);
            }

            appContext.Run(shell.Window);
        }
        finally
        {
            // Stop any in-flight startup hydration from mutating the UI while the loop is
            // shutting down; a queued-but-unrun mutation observes the cancelled token.
            var shutdownWatch = Stopwatch.StartNew();
            _logger.LogInformation("TUI host shutdown started");
            hostLifetime.Cancel();
            try
            {
                renderCoordinator.Dispose();
                inlineSelection.Dispose();
                harnessLifecycle.HarnessSubscription.Dispose();
                shell.Window.Dispose();
                Application.Shutdown();
            }
            finally
            {
                Console.CancelKeyPress -= consoleCancelKeyPressHandler;
                terminalScreenSession.Exit();
            }
            _logger.LogInformation("TUI host shutdown completed durationMs={DurationMs}", shutdownWatch.ElapsedMilliseconds);
        }
        return 0;
    }

    private sealed class TerminalCancelKeyPressEvent(ConsoleCancelEventArgs args) : ITerminalCancelKeyPressEvent
    {
        public bool Cancel
        {
            get => args.Cancel;
            set => args.Cancel = value;
        }
    }
}
