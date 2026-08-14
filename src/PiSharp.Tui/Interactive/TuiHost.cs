using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Extensions;
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
    private readonly ILogger<TuiHost> _logger = options.LoggerFactory?.CreateLogger<TuiHost>() ?? NullLogger<TuiHost>.Instance;
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
        ConsoleTerminalSessionLifetimeEvents.LoggerFactory = options.LoggerFactory;
        var terminalScreenSession = options.TerminalScreenSession ?? AnsiTerminalScreenSession.CreateDefault(options.LoggerFactory);
        terminalScreenSession.Enter();
        try
        {
            var driver = options.ConsoleDriver;
            var driverName = driver is null ? TuiConsoleDriverName.DefaultForCurrentPlatform() : "FakeDriver";
            if (driver is null) TuiConsoleDriverName.PrepareConsoleForDriver(driverName);
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
        catch
        {
            terminalScreenSession.Exit();
            throw;
        }
        TuiTheme.Apply(options.Theme);
        TuiShortcutRegistrar.LoggerFactory = options.LoggerFactory;

        var keybindingsStore = new TuiKeybindingStore(options.KeybindingsDefaults ?? TuiBuiltInShortcutCatalog.Bindings);
        TuiShortcutRegistrar.DefaultStore = keybindingsStore;
        var initialKeybindingsDiagnostics = new List<string>();
        if (!string.IsNullOrEmpty(options.KeybindingsPath) && File.Exists(options.KeybindingsPath))
        {
            if (KeybindingsLoader.TryReadFile(options.KeybindingsPath, out var keybindingsJson, out var keybindingsError))
            {
                keybindingsStore.Reload(keybindingsJson);
                initialKeybindingsDiagnostics.AddRange(keybindingsStore.Diagnostics);
            }
            else if (keybindingsError is not null)
            {
                initialKeybindingsDiagnostics.Add(keybindingsError);
            }
        }

        var appContext = new TerminalGuiApplicationContext();

        var startupHarness = options.GetCurrentHarness?.Invoke() ?? options.Harness;
        var state = TuiRenderState.Empty(
            options.SessionId, options.SessionFile,
            startupHarness.Model, startupHarness.ThinkingLevel,
            await options.GetSessionNameAsync(cancellationToken));
        if (options.GetSessionSnapshotAsync is not null)
        {
            var snapshot = await options.GetSessionSnapshotAsync(cancellationToken);
            state = state.HydrateSession(snapshot.SessionId, snapshot.SessionFile, snapshot.SessionName, snapshot.BranchEntries);
        }
        foreach (var message in options.StartupMessages ?? [])
            state = state.AppendSystem(message, pinToTop: true, expiresAfter: TransientSystemMessageLifetime);
        foreach (var diagnostic in initialKeybindingsDiagnostics)
            state = state.AppendSystem(diagnostic, isError: true);

        var shell = new TuiShellView(options.LoggerFactory);
        shell.ProfilingCounters = options.ProfilingCounters;
        var chatPipeline = new ChatRowRenderPipeline(options.GetExtensionRegistry?.Invoke(), loggerFactory: options.LoggerFactory);
        shell.Chat.TranscriptItemRenderer = (item, renderState, width) =>
        {
            options.ProfilingCounters?.Increment(TuiProfilingCounterNames.TranscriptItemRender);
            return chatPipeline.Render(item, renderState, width);
        };
        shell.Chat.ProfilingCounters = options.ProfilingCounters;

        async Task<TuiSessionSnapshot?> LoadSessionSnapshotAsync(CancellationToken token)
            => options.GetSessionSnapshotAsync is null ? null : await options.GetSessionSnapshotAsync(token);

        void ApplySessionSnapshot(TuiSessionSnapshot snapshot, bool preserve = false)
            => state = TuiSessionSwitch.ApplySnapshot(state, snapshot, preserve);

        var headerExpanded = false;
        var footerSnapshotProvider = new TuiFooterSnapshotProvider(loggerFactory: options.LoggerFactory);
        var timingOptions = options.TimingOptions ?? TuiTimingOptions.Default;

        TuiShortcutActionHandler? shortcutActionsRef = null;
        void InvokeMenuCommand(string command)
        {
            if (shortcutActionsRef is not null)
                shortcutActionsRef.DispatchShortcutCommand(command);
        }
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext,
            () => options.FooterSnapshot?.Invoke(state) ?? footerSnapshotProvider.CreateSnapshot(state, options.WorkingDirectory ?? Environment.CurrentDirectory),
            options.ProfilingCounters,
            renderFrameInterval: timingOptions.RenderFrameInterval,
            getHeaderExpanded: () => headerExpanded,
            getExtensionLoadStatus: () => options.GetExtensionLoadStatus?.Invoke(),
            getActiveTools: () => (options.GetCurrentHarness?.Invoke() ?? options.Harness).ActiveToolNames,
            invokeCommand: InvokeMenuCommand,
            cancellationToken: cancellationToken,
            loggerFactory: options.LoggerFactory);

        var inlineSelection = new TuiInlineSelectionCoordinator(shell.Prompt, () => renderCoordinator.RequestRender(), appContext.Post);
        renderCoordinator.SelectionSessionGetter = () => inlineSelection.CurrentSession;

        var bridge = new ExtensionUiBridgeHost(shell.Window,
            updateState: u => { state = u(state); renderCoordinator.RequestRender(); },
            getEditorText: () => shell.Prompt.PromptText,
            setEditorText: t => shell.Prompt.SetPromptText(t),
            getState: () => state,
            loggerFactory: options.LoggerFactory)
        {
            DispatchUi = appContext.Post,
            RestoreFocus = () => shell.Prompt.FocusAtEnd(),
            ShowNotification = message =>
            {
                state = state.AppendSystem(message, expiresAfter: TransientSystemMessageLifetime);
                renderCoordinator.RequestRender();
            }
        };
        renderCoordinator.IsInputCaptured = () => bridge.HasActiveCustomUi;
        options.ConfigureUiBridge?.Invoke(bridge);
        var extensionUi = new TuiExtensionUi(bridge, SelectInlineWithLoggingAsync);

        var shortcutController = new TuiShortcutController(new TuiShortcutControllerOptions(
            () => options.GetExtensionShortcuts?.Invoke() ?? [], extensionUi, _ => { }));
        _ = shortcutController.BuildExtensionShortcutBindings();

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
            () => state,
            next => { state = next; renderCoordinator.RequestRender(); },
            () => (options.GetCurrentHarness?.Invoke() ?? startupHarness).Abort(),
            () => appContext.Post(() => appContext.RequestStop(shell.Window)),
            () => TuiHotkeyText.RenderFromDescriptors(keybindingsStore.CommandDescriptors, options.GetExtensionShortcuts?.Invoke() ?? []),
            commandText => new TuiCommandDispatchRequest(commandText, SelectInlineWithLoggingAsync,
                (string text, CancellationToken ct) => PromptDialog.InputAsync(text, ct, dispatcher: appContext),
                (msg, isErr, _) =>
                {
                    state = state.AppendSystem(msg, isErr, expiresAfter: isErr ? null : TransientSystemMessageLifetime);
                    renderCoordinator.RequestRender();
                    return Task.CompletedTask;
                },
                (a, b, c, d) => SessionSelectorDialog.SelectAsync(a, b, c, d, appContext: appContext)),
            options.DispatchCommandAsync,
            token => sessionRefresh?.Invoke(token) ?? Task.CompletedTask,
            () => (options.GetCurrentHarness?.Invoke() ?? startupHarness).Phase,
            OnAbortRequested: () => onAbortRequested?.Invoke()),
            options.LoggerFactory);

        var sessionContext = new TuiSessionContext { CurrentHarness = startupHarness, HeaderExpanded = headerExpanded };
        var stateGateway = new TuiStateGateway(() => state, s => state = s, renderCoordinator, appContext, cancellationToken);
        var harnessLifecycle = new TuiHarnessLifecycleCoordinator(
            sessionContext, options, appContext, renderCoordinator,
            () => state, s => state = s, LoadSessionSnapshotAsync, ApplySessionSnapshot, options.LoggerFactory);
        var shortcutActions = new TuiShortcutActionHandler(
            shell, inlineSelection, appContext, stateGateway, sessionContext, options, cancellationToken, options.LoggerFactory);
        var transcriptController = new TuiTranscriptInteractionController(
            shell, options, appContext, renderCoordinator, stateGateway, sessionContext,
            LoadSessionSnapshotAsync, ApplySessionSnapshot, cancellationToken, options.LoggerFactory);
        var submissionCoordinator = new TuiPromptSubmissionCoordinator(
            shell, inlineSelection, stateGateway, sessionContext, options,
            new PromptFileReferenceCompletionProvider(
                options.WorkingDirectory ?? Environment.CurrentDirectory,
                gitVisibility: GitVisibilityService.TryCreate(Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory), options.LoggerFactory?.CreateLogger(nameof(GitVisibilityService)) ?? NullLogger.Instance),
                loggerFactory: options.LoggerFactory,
                profilingCounters: options.ProfilingCounters),
            options.LoggerFactory);
        submissionCoordinator.CommandController = commandController;
        shortcutActions.CommandController = commandController;
        shortcutActionsRef = shortcutActions;

        sessionRefresh = harnessLifecycle.RefreshAfterPossibleSessionChangeAsync;
        onAbortRequested = () => sessionContext.AbortPending = true;
        options.OnHarnessReplaced = harnessLifecycle.RefreshAfterPossibleSessionChangeAsync;

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
            shortcutActions.ReportShortcutError, cancellationToken, () => bridge.HasActiveCustomUi, () => shell.HasActiveEditorComponent, options.LoggerFactory);
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
            loggerFactory: options.LoggerFactory);
        inputRouter.Attach();

        keybindingsStore.Changed += () => appContext.Post(() => renderCoordinator.RequestRender());
        using var keybindingsWatcher = string.IsNullOrEmpty(options.KeybindingsPath)
            ? null
            : new KeybindingsWatcher(options.KeybindingsPath, keybindingsStore, appContext.Post, shortcutActions.ReportShortcutError);

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
        renderCoordinator.RequestRender(cancellationToken);

        if (options.PostStartupChecksAsync is not null)
        {
            Task InjectMessage(string message) =>
                appContext.InvokeAsync(() =>
                {
                    state = state.AppendSystem(message, pinToTop: false, expiresAfter: PostStartupMessageLifetime);
                    renderCoordinator.RequestRender();
                }, cancellationToken);
            _ = Task.Run(() => options.PostStartupChecksAsync(InjectMessage, cancellationToken), cancellationToken);
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
                    () => state,
                    () => renderCoordinator.RequestRender(),
                    InvokeMenuCommand);

                await options.BeforeRunAsync(runContext, cancellationToken);
            }

            appContext.Run(shell.Window);
        }
        finally
        {
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
