using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Harness;
using PiSharp.Tui.Interactive.Input;
using PiSharp.Tui.Interactive.Rendering;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.SessionSelection;
using PiSharp.Tui.Interactive.Shell;
using PiSharp.Tui.Tests.TestLogging;
using System.Runtime.CompilerServices;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class FakeTuiDispatcher : ITuiDispatcher
{
    private readonly List<Action> _posted = [];
    private readonly Dictionary<object, (TimeSpan Interval, Func<bool> Callback)> _timeouts = [];
    private int _nextToken;

    public IReadOnlyList<Action> Posted => _posted;
    public int TimeoutCount => _timeouts.Count;

    public void Post(Action action) => _posted.Add(action);

    public void PumpPosted()
    {
        var actions = _posted.ToArray();
        _posted.Clear();
        foreach (var action in actions) action();
    }

    public object AddTimeout(TimeSpan interval, Func<bool> callback)
    {
        var token = ++_nextToken;
        _timeouts[token] = (interval, callback);
        return token;
    }

    public void RemoveTimeout(object token) => _timeouts.Remove(token);

    public void AdvanceTimeout(object token)
    {
        if (_timeouts.TryGetValue(token, out var entry))
        {
            if (!entry.Callback()) _timeouts.Remove(token);
        }
    }
}

public sealed class FakeTuiApplicationContext : ITuiApplicationContext
{
    private readonly FakeTuiDispatcher _dispatcher = new();
    private EventHandler<Key>? _keyDown;
    private EventHandler<SizeChangedEventArgs>? _sizeChanging;

    public FakeTuiDispatcher Dispatcher => _dispatcher;
    public Action<Toplevel>? OnRun { get; set; }
    public Toplevel? LastRunView { get; private set; }
    public int KeyDownHandlerCount => _keyDown?.GetInvocationList().Length ?? 0;
    public int SizeChangingHandlerCount => _sizeChanging?.GetInvocationList().Length ?? 0;
    public int StopRequestCount { get; private set; }

    public void Post(Action action) => _dispatcher.Post(action);
    public object AddTimeout(TimeSpan interval, Func<bool> callback) => _dispatcher.AddTimeout(interval, callback);
    public void RemoveTimeout(object token) => _dispatcher.RemoveTimeout(token);
    public void RequestStop(Toplevel view) => StopRequestCount++;
    public void Run(Toplevel view)
    {
        LastRunView = view;
        OnRun?.Invoke(view);
    }

    public event EventHandler<Key>? KeyDown
    {
        add => _keyDown += value;
        remove => _keyDown -= value;
    }

    public event EventHandler<SizeChangedEventArgs>? SizeChanging
    {
        add => _sizeChanging += value;
        remove => _sizeChanging -= value;
    }
}

public sealed class TuiApplicationContextTests
{
    private sealed class RecordingCancelKeyPressEvent : ITerminalCancelKeyPressEvent
    {
        public bool Cancel { get; set; }
    }

    [Fact]
    public void ConsoleCancelKeyPressPostsCtrlCShortcutAndCancelsInterrupt()
    {
        var context = new FakeTuiApplicationContext();
        var cancelEvent = new RecordingCancelKeyPressEvent();
        var shortcutCalled = false;

        TuiHost.HandleConsoleCancelKeyPress(cancelEvent, context, () => shortcutCalled = true);

        Assert.True(cancelEvent.Cancel);
        Assert.False(shortcutCalled);
        Assert.Single(context.Dispatcher.Posted);

        context.Dispatcher.PumpPosted();

        Assert.True(shortcutCalled);
    }

    [Fact]
    public void FakeDispatcherPostRecordsAction()
    {
        var dispatcher = new FakeTuiDispatcher();
        var executed = false;

        dispatcher.Post(() => executed = true);

        Assert.Single(dispatcher.Posted);
        Assert.False(executed);

        dispatcher.Posted[0]();

        Assert.True(executed);
    }

    [Fact]
    public void TerminalGuiDispatcherPostWakesLoopAfterSchedulingAction()
    {
        var calls = new List<string>();
        var dispatcher = new TerminalGuiDispatcher(
            action => calls.Add("invoke"),
            () => calls.Add("wakeup"));

        dispatcher.Post(() => { });

        Assert.Equal(["invoke", "wakeup"], calls);
    }

    [Fact]
    public void FakeDispatcherAddTimeoutReturnsTrackedToken()
    {
        var dispatcher = new FakeTuiDispatcher();
        var invoked = false;

        var token = dispatcher.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            invoked = true;
            return false;
        });

        Assert.NotNull(token);
        dispatcher.AdvanceTimeout(token);
        Assert.True(invoked);
    }

    [Fact]
    public void FakeDispatcherRemoveTimeoutPreventsCallbackInvocation()
    {
        var dispatcher = new FakeTuiDispatcher();
        var invoked = false;

        var token = dispatcher.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            invoked = true;
            return false;
        });

        dispatcher.RemoveTimeout(token);
        dispatcher.AdvanceTimeout(token);

        Assert.False(invoked);
    }

    [Fact]
    public void SettingsDialogUsesProvidedDispatcher()
    {
        var dispatcher = new FakeTuiDispatcher();

        SettingsDialog.ShowAsync("title", "content", dispatcher: dispatcher);

        Assert.Single(dispatcher.Posted);
    }

    [Fact]
    public void SessionTreeDialogUsesProvidedDispatcher()
    {
        var dispatcher = new FakeTuiDispatcher();

        SessionTreeDialog.ShowAsync("tree", dispatcher: dispatcher);

        Assert.Single(dispatcher.Posted);
    }

    [Fact]
    public async Task BridgeNotifyAsyncIsIncompleteUntilPumped()
    {
        var dispatcher = new FakeTuiDispatcher();
        string? notification = null;
        var window = new Window();
        var bridge = new ExtensionUiBridgeHost(window)
        {
            DispatchUi = dispatcher.Post,
            ShowNotification = message => notification = message
        };

        var task = bridge.NotifyAsync("test");

        Assert.Single(dispatcher.Posted);
        Assert.False(task.IsCompleted);
        Assert.Null(notification);

        dispatcher.PumpPosted();

        await task;
        Assert.Equal("test", notification);
    }

    [Fact]
    public async Task BridgeSetStatusAsyncDispatchesThroughUiAction()
    {
        var dispatcher = new FakeTuiDispatcher();
        var window = new Window();
        var bridge = new ExtensionUiBridgeHost(window)
        {
            DispatchUi = dispatcher.Post
        };

        var task = bridge.SetStatusAsync("ext1", "running");

        Assert.Single(dispatcher.Posted);
        Assert.False(task.IsCompleted);

        dispatcher.PumpPosted();

        await task;
    }

    [Fact]
    public async Task BridgeSetWidgetAsyncDispatchesThroughUiAction()
    {
        var dispatcher = new FakeTuiDispatcher();
        var window = new Window();
        var bridge = new ExtensionUiBridgeHost(window)
        {
            DispatchUi = dispatcher.Post
        };

        var task = bridge.SetWidgetAsync("ext1", new ExtensionWidgetState("text", "content"));

        Assert.Single(dispatcher.Posted);
        Assert.False(task.IsCompleted);

        dispatcher.PumpPosted();

        await task;
    }

    [Fact]
    public async Task BridgeSetEditorTextAsyncDispatchesThroughUiAction()
    {
        var dispatcher = new FakeTuiDispatcher();
        var window = new Window();
        var bridge = new ExtensionUiBridgeHost(window)
        {
            DispatchUi = dispatcher.Post
        };

        var task = bridge.SetEditorTextAsync("hello");

        Assert.Single(dispatcher.Posted);
        Assert.False(task.IsCompleted);

        dispatcher.PumpPosted();

        await task;
    }

    [Fact]
    public async Task BridgeClearSourceAsyncDispatchesThroughUiAction()
    {
        var dispatcher = new FakeTuiDispatcher();
        var window = new Window();
        var bridge = new ExtensionUiBridgeHost(window)
        {
            DispatchUi = dispatcher.Post
        };

        var task = bridge.ClearSourceAsync("ext1");

        Assert.Single(dispatcher.Posted);
        Assert.False(task.IsCompleted);

        dispatcher.PumpPosted();

        await task;
    }

    // ExtensionUiBridgeHost without DispatchUi falls back to Application.Invoke,
    // which requires a running Terminal.Gui app and is not unit-testable here.
    // The wired-path tests above cover DispatchUi forwarding for representative methods.

    [Fact]
    public void TuiRenderSchedulerAcceptsITuiDispatcher()
    {
        var dispatcher = new FakeTuiDispatcher();
        var renders = 0;

        var scheduler = new TuiRenderScheduler(dispatcher);

        scheduler.RequestRender(() => renders++);

        Assert.Single(dispatcher.Posted);
        Assert.Equal(0, renders);

        dispatcher.Posted[0]();

        Assert.Equal(1, renders);
    }

    [Fact]
    public async Task TuiDispatcherInvokeAsyncCompletesAfterPostedActionRuns()
    {
        var dispatcher = new FakeTuiDispatcher();
        var invoked = false;

        var task = dispatcher.InvokeAsync(() => invoked = true);

        Assert.Single(dispatcher.Posted);
        Assert.False(task.IsCompleted);
        Assert.False(invoked);

        dispatcher.PumpPosted();

        await task;
        Assert.True(invoked);
    }

    [Fact]
    public async Task TuiDispatcherInvokeAsyncCancellationUsesProvidedTokenBeforePostedActionRuns()
    {
        var dispatcher = new FakeTuiDispatcher();
        using var cancellation = new CancellationTokenSource();

        var task = dispatcher.InvokeAsync(() => { }, cancellation.Token);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => task);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task PromptDialogInputAsyncReturnsEnteredTextFromAppContextDialog()
    {
        var appContext = new FakeTuiApplicationContext
        {
            OnRun = view =>
            {
                var input = FindDescendant<TextField>(view);
                Assert.NotNull(input);
                input.Text = "sk-test";
                input.NewKeyDownEvent(Key.Enter);
            }
        };

        var task = PromptDialog.InputAsync("prompt", dispatcher: appContext);

        Assert.Single(appContext.Dispatcher.Posted);
        Assert.False(task.IsCompleted);

        appContext.Dispatcher.PumpPosted();

        var result = await task;

        Assert.Equal("sk-test", result);
        Assert.NotNull(appContext.LastRunView);
        Assert.Equal(1, appContext.StopRequestCount);
    }

    [Fact]
    public async Task PromptDialogInputAsyncPrepopulatesInitialValue()
    {
        var appContext = new FakeTuiApplicationContext
        {
            OnRun = view =>
            {
                var input = FindDescendant<TextField>(view);
                Assert.NotNull(input);
                Assert.Equal("preset", input.Text?.ToString());
                input.NewKeyDownEvent(Key.Enter);
            }
        };

        var task = PromptDialog.InputAsync("prompt", "preset", dispatcher: appContext);

        Assert.Single(appContext.Dispatcher.Posted);
        Assert.False(task.IsCompleted);

        appContext.Dispatcher.PumpPosted();

        var result = await task;

        Assert.Equal("preset", result);
    }

    private static T? FindDescendant<T>(View view) where T : View
    {
        foreach (var child in view.Subviews)
        {
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    [Fact]
    public void SelectorDialogSelectAsyncUsesProvidedAppContext()
    {
        var appContext = new FakeTuiApplicationContext();

        var task = SelectorDialog.SelectAsync("title", ["a", "b"], appContext: appContext);

        Assert.Single(appContext.Dispatcher.Posted);
    }

    [Fact]
    public async Task SessionSelectorLoadAllSessionsPostsUpdateOnCompletion()
    {
        var dispatcher = new FakeTuiDispatcher();
        var allSessionsData = new[]
        {
            new JsonlSessionMetadata("s1", DateTimeOffset.UtcNow, "/repo", "/sessions/s1.jsonl"),
            new JsonlSessionMetadata("s2", DateTimeOffset.UtcNow, "/repo", "/sessions/s2.jsonl")
        };
        var loadTcs = new TaskCompletionSource<IReadOnlyList<JsonlSessionMetadata>>();
        IReadOnlyList<JsonlSessionMetadata>? loaded = null;

        var loadTask = SessionSelectorDialog.LoadAllSessionsInternalAsync(
            _ => loadTcs.Task,
            CancellationToken.None,
            dispatcher,
            sessions => loaded = sessions);

        Assert.False(loadTask.IsCompleted);
        Assert.Empty(dispatcher.Posted);
        Assert.Null(loaded);

        loadTcs.SetResult(allSessionsData);
        await loadTask;

        Assert.Single(dispatcher.Posted);

        dispatcher.PumpPosted();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("s1", loaded[0].Id);
        Assert.Equal("s2", loaded[1].Id);
    }

    [Fact]
    public async Task SessionSelectorLoadAllSessionsCancellationPreventsUpdate()
    {
        var dispatcher = new FakeTuiDispatcher();
        var cts = new CancellationTokenSource();
        IReadOnlyList<JsonlSessionMetadata>? loaded = null;

        var loadTask = SessionSelectorDialog.LoadAllSessionsInternalAsync(
            ct => Task.FromException<IReadOnlyList<JsonlSessionMetadata>>(new OperationCanceledException(ct)),
            cts.Token,
            dispatcher,
            sessions => loaded = sessions);

        await loadTask;

        Assert.Empty(dispatcher.Posted);
        Assert.Null(loaded);
    }

    [Fact]
    public void ToggleToAllDetectsLoadingState()
    {
        var controller = new SessionSelectorController();
        controller.Scope = SessionSelectorScope.All;

        bool started = controller.TryStartLoading(
            _ => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            CancellationToken.None, new FakeTuiDispatcher(),
            out var title, out var rows);

        Assert.True(started);
        Assert.Equal("Resume Session (Loading All)", title);
        Assert.Equal(["  Loading sessions..."], rows);
    }

    [Fact]
    public async Task ToggleToAllCompletionUpdatesAllSessions()
    {
        var controller = new SessionSelectorController();
        var dispatcher = new FakeTuiDispatcher();
        var loadTcs = new TaskCompletionSource<IReadOnlyList<JsonlSessionMetadata>>();
        int refreshCount = 0;
        controller.OnSessionsLoaded = () => refreshCount++;
        controller.Scope = SessionSelectorScope.All;

        controller.TryStartLoading(
            _ => loadTcs.Task, CancellationToken.None, dispatcher,
            out _, out _);

        Assert.NotNull(controller.LoadingTask);
        Assert.False(controller.LoadingTask.IsCompleted);
        Assert.Null(controller.AllSessions);

        loadTcs.SetResult([new JsonlSessionMetadata("s1", DateTimeOffset.UtcNow, "/repo", "/sessions/s1.jsonl")]);

        await controller.LoadingTask;

        Assert.False(controller.LoadingTask.IsFaulted);
        Assert.True(controller.LoadingTask.IsCompleted);

        dispatcher.PumpPosted();

        Assert.NotNull(controller.AllSessions);
        Assert.Single(controller.AllSessions);
        Assert.Equal("s1", controller.AllSessions[0].Id);
        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public async Task CompletingLoadAfterDeactivationDoesNotMutateControllerState()
    {
        var controller = new SessionSelectorController();
        var dispatcher = new FakeTuiDispatcher();
        var loadTcs = new TaskCompletionSource<IReadOnlyList<JsonlSessionMetadata>>();
        int refreshCount = 0;
        controller.OnSessionsLoaded = () => refreshCount++;
        controller.Scope = SessionSelectorScope.All;

        controller.TryStartLoading(
            _ => loadTcs.Task, CancellationToken.None, dispatcher,
            out _, out _);

        controller.IsActive = false;

        loadTcs.SetResult([new JsonlSessionMetadata("s1", DateTimeOffset.UtcNow, "/repo", "/sessions/s1.jsonl")]);

        await controller.LoadingTask!;

        dispatcher.PumpPosted();

        Assert.Null(controller.AllSessions);
        Assert.Equal(0, refreshCount);
    }

    [Fact]
    public async Task SessionSelectorSelectAsyncUsesProvidedAppContext()
    {
        var appContext = new FakeTuiApplicationContext
        {
            OnRun = _ => { }
        };
        var currentLoaded = false;

        var task = SessionSelectorDialog.SelectAsync(
            ct =>
            {
                currentLoaded = true;
                return Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]);
            },
            ct => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            null,
            CancellationToken.None,
            appContext);

        Assert.True(currentLoaded);
        Assert.Single(appContext.Dispatcher.Posted);

        appContext.Dispatcher.PumpPosted();

        var result = await task;
        Assert.Null(result);
    }

    [Fact]
    public async Task SessionSelectorCancellationPreventsLoadAndUiPost()
    {
        var appContext = new FakeTuiApplicationContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var loadCalled = false;

        var task = SessionSelectorDialog.SelectAsync(
            ct =>
            {
                loadCalled = true;
                return Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]);
            },
            ct => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            null,
            cts.Token,
            appContext);

        await Assert.ThrowsAsync<TaskCanceledException>(() => task);
        Assert.False(loadCalled);
        Assert.Empty(appContext.Dispatcher.Posted);
    }

    private sealed class FakeSessionSelectorRuntime : PiSharp.Tui.Interactive.Components.ISessionSelectorRuntime
    {
        public int EnterCount { get; private set; }
        public int ExitCount { get; private set; }

        public void Enter() => EnterCount++;
        public void Exit() => ExitCount++;
    }

    [Fact]
    public async Task SessionSelectorStandaloneRuntimeEntersAndExits()
    {
        var runtime = new FakeSessionSelectorRuntime();
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };

        var result = await SessionSelectorDialog.SelectStandaloneInternalAsync(
            ct => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            ct => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            null,
            runtime,
            appContext: appContext);

        Assert.Equal(1, runtime.EnterCount);
        Assert.Equal(1, runtime.ExitCount);
        Assert.Null(result);
    }

    private sealed class FakeThrowingSessionSelectorRuntime : PiSharp.Tui.Interactive.Components.ISessionSelectorRuntime
    {
        public int EnterCount { get; private set; }
        public int ExitCount { get; private set; }

        public void Enter()
        {
            EnterCount++;
            throw new InvalidOperationException("enter failed");
        }

        public void Exit() => ExitCount++;
    }

    [Fact]
    public async Task SessionSelectorStandaloneRuntimeExitCalledWhenEnterThrows()
    {
        var runtime = new FakeThrowingSessionSelectorRuntime();
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SessionSelectorDialog.SelectStandaloneInternalAsync(
                ct => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
                ct => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
                null,
                runtime,
                appContext: appContext));

        Assert.Equal(1, runtime.EnterCount);
        Assert.Equal(1, runtime.ExitCount);
    }

    [Fact]
    public void TerminalGuiSessionSelectorRuntimeExitWithoutEnterIsSafe()
    {
        var runtime = new PiSharp.Tui.Interactive.Components.TerminalGuiSessionSelectorRuntime();

        var exception = Record.Exception(() => runtime.Exit());

        Assert.Null(exception);
    }

    [Fact]
    public async Task SessionSelectorLoadAllSessionsNonCancellationFailurePostsEmptyList()
    {
        var dispatcher = new FakeTuiDispatcher();
        IReadOnlyList<JsonlSessionMetadata>? loaded = null;

        var loadTask = SessionSelectorDialog.LoadAllSessionsInternalAsync(
            _ => Task.FromException<IReadOnlyList<JsonlSessionMetadata>>(new InvalidOperationException("load failed")),
            CancellationToken.None,
            dispatcher,
            sessions => loaded = sessions);

        await loadTask;

        Assert.Single(dispatcher.Posted);
        dispatcher.PumpPosted();
        Assert.NotNull(loaded);
        Assert.Empty(loaded);
    }

    [Fact]
    public void TryStartLoadingReceivesCancellationTokenThatRespondsToCtsCancel()
    {
        var controller = new SessionSelectorController();
        var dispatcher = new FakeTuiDispatcher();
        using var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        controller.Scope = SessionSelectorScope.All;
        controller.TryStartLoading(
            ct =>
            {
                capturedToken = ct;
                return Task.FromException<IReadOnlyList<JsonlSessionMetadata>>(
                    new OperationCanceledException(ct));
            },
            cts.Token,
            dispatcher,
            out _, out _);

        Assert.False(capturedToken.IsCancellationRequested);
        Assert.Equal(cts.Token, capturedToken);

        cts.Cancel();

        Assert.True(capturedToken.IsCancellationRequested);
        Assert.NotNull(controller.LoadingTask);
        Assert.True(controller.LoadingTask.IsCompleted);
        Assert.Null(controller.AllSessions);
        Assert.Empty(dispatcher.Posted);
    }

    [Fact]
    public void SessionSelectorWindowInitialRefreshShowsCurrentSessionRows()
    {
        var sessions = new[]
        {
            new JsonlSessionMetadata("s1", DateTimeOffset.UtcNow, "/repo", "/sessions/s1.jsonl", firstMessage: "session-one"),
            new JsonlSessionMetadata("s2", DateTimeOffset.UtcNow, "/repo", "/sessions/s2.jsonl", firstMessage: "session-two")
        };
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };
        var window = new SessionSelectorWindow(
            sessions, _ => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            appContext, CancellationToken.None);

        window.Refresh();

        Assert.Equal(2, window.Rows.Count);
        Assert.Contains(window.Rows, r => r.Contains("session-one", StringComparison.Ordinal));
        Assert.Contains(window.Rows, r => r.Contains("session-two", StringComparison.Ordinal));
        Assert.Equal("Resume Session (Current Folder)", window.DialogTitle);
    }

    [Fact]
    public void SessionSelectorWindowToggleToAllEntersLoadingState()
    {
        var sessions = Array.Empty<JsonlSessionMetadata>();
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };
        var loadTcs = new TaskCompletionSource<IReadOnlyList<JsonlSessionMetadata>>();
        var window = new SessionSelectorWindow(
            sessions, _ => loadTcs.Task,
            appContext, CancellationToken.None);

        window.Refresh();
        Assert.Equal(SessionSelectorScope.Current, window.Scope);

        window.ToggleScope();

        Assert.Equal(SessionSelectorScope.All, window.Scope);
        Assert.Equal("Resume Session (Loading All)", window.DialogTitle);
        Assert.Single(window.Rows);
        Assert.Contains(window.Rows, r => r.Contains("Loading", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionSelectorWindowToggleBackFromAllReturnsToRefresh()
    {
        var sessions = new[]
        {
            new JsonlSessionMetadata("s1", DateTimeOffset.UtcNow, "/repo", "/sessions/s1.jsonl", firstMessage: "session-one")
        };
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };
        var loadTcs = new TaskCompletionSource<IReadOnlyList<JsonlSessionMetadata>>();
        var window = new SessionSelectorWindow(
            sessions, _ => loadTcs.Task,
            appContext, CancellationToken.None);

        window.Refresh();
        window.ToggleScope();
        Assert.Equal(SessionSelectorScope.All, window.Scope);

        window.ToggleScope();

        Assert.Equal(SessionSelectorScope.Current, window.Scope);
        Assert.Equal("Resume Session (Current Folder)", window.DialogTitle);
    }

    [Fact]
    public void SessionSelectorWindowAcceptSelectsRowAndRequestsStop()
    {
        var sessions = new[]
        {
            new JsonlSessionMetadata("s1", DateTimeOffset.UtcNow, "/repo", "/sessions/s1.jsonl", firstMessage: "selected-session")
        };
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };
        var window = new SessionSelectorWindow(
            sessions, _ => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            appContext, CancellationToken.None);

        window.Refresh();
        Assert.Equal(0, appContext.StopRequestCount);

        window.Accept();

        Assert.Equal(1, appContext.StopRequestCount);
        Assert.NotNull(window.SelectedResult);
        Assert.Equal("s1", window.SelectedResult!.Id);
    }

    [Fact]
    public void SessionSelectorWindowShowDeactivatesControllerAndDisposesCleanly()
    {
        var sessions = Array.Empty<JsonlSessionMetadata>();
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };
        var window = new SessionSelectorWindow(
            sessions, _ => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            appContext, CancellationToken.None);

        var result = window.Show();

        Assert.Null(result);
        Assert.False(window.IsControllerActive);
    }

    [Fact]
    public async Task SessionSelectorWindowAllSessionLoadUpdatesRowsAfterDispatcherPost()
    {
        var sessions = Array.Empty<JsonlSessionMetadata>();
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };
        var allSessions = new[]
        {
            new JsonlSessionMetadata("all1", DateTimeOffset.UtcNow, "/other", "/sessions/all1.jsonl", firstMessage: "loaded-session")
        };
        var loadTcs = new TaskCompletionSource<IReadOnlyList<JsonlSessionMetadata>>();
        var window = new SessionSelectorWindow(
            sessions, _ => loadTcs.Task,
            appContext, CancellationToken.None);

        window.Refresh();
        Assert.Single(window.Rows);
        Assert.Contains(window.Rows, r => r.Contains("No sessions", StringComparison.Ordinal));

        window.ToggleScope();
        Assert.Contains(window.Rows, r => r.Contains("Loading", StringComparison.Ordinal));
        Assert.NotNull(window.LoadingTask);

        loadTcs.SetResult(allSessions);
        await window.LoadingTask;

        appContext.Dispatcher.PumpPosted();

        window.Refresh();
        Assert.NotEmpty(window.Rows);
        Assert.Contains(window.Rows, r => r.Contains("loaded-session", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionSelectorWindowHintTextParity()
    {
        var appContext = new FakeTuiApplicationContext { OnRun = _ => { } };
        var window = new SessionSelectorWindow(
            [], _ => Task.FromResult<IReadOnlyList<JsonlSessionMetadata>>([]),
            appContext, CancellationToken.None);

        Assert.Equal("Type to search sessions. Tab toggles Current/All. ↑/↓ moves, Enter selects, Esc cancels.", window.HintText);
    }
}

public sealed class TuiDisposalTests
{
    [Fact]
    public void RenderCoordinatorDisposeRemovesAllTimeouts()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            PiSharp.Abstractions.Options.ThinkingLevel.Off, null);

        Assert.Equal(0, appContext.Dispatcher.TimeoutCount);
        Assert.Equal(0, appContext.SizeChangingHandlerCount);

        var coordinator = CreateRenderCoordinator(appContext, () => state, s => state = s);
        coordinator.Initialize();

        Assert.True(appContext.Dispatcher.TimeoutCount > 0, "Should have registered timeouts after Initialize");
    }

    [Fact]
    public void RenderCoordinatorDisposeRemovesSizeChangingHandler()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            PiSharp.Abstractions.Options.ThinkingLevel.Off, null);

        Assert.Equal(0, appContext.SizeChangingHandlerCount);

        var coordinator = CreateRenderCoordinator(appContext, () => state, s => state = s);
        coordinator.Initialize();

        Assert.Equal(1, appContext.SizeChangingHandlerCount);

        coordinator.Dispose();

        Assert.Equal(0, appContext.SizeChangingHandlerCount);
    }

    [Fact]
    public void RenderCoordinatorDoubleDisposeIsIdempotent()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            PiSharp.Abstractions.Options.ThinkingLevel.Off, null);

        var coordinator = CreateRenderCoordinator(appContext, () => state, s => state = s);
        coordinator.Initialize();

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Equal(0, appContext.Dispatcher.TimeoutCount);
        Assert.Equal(0, appContext.SizeChangingHandlerCount);
    }

    [Fact]
    public void InputCoordinatorDisposeUnsubscribesKeyDownHandlers()
    {
        var appContext = new FakeTuiApplicationContext();
        using var window = new Window();
        using var prompt = new PromptEditor();

        Assert.Equal(0, appContext.KeyDownHandlerCount);

        var coordinator = new PiSharp.Tui.Interactive.Input.TuiInputCoordinator(
            window, new PiSharp.Tui.Interactive.Components.ChatView(),
            prompt, () => { },
            PiSharp.Tui.Interactive.TuiShortcutDispatcher.CreateDefaultAppDispatcher(),
            new PiSharp.Tui.Interactive.TuiShortcutContext(),
            new PiSharp.Tui.Interactive.TuiShortcutController(
                new PiSharp.Tui.Interactive.TuiShortcutControllerOptions(
                    () => [], null!, _ => { })),
            _ => { });

        coordinator.Attach();

        Assert.Equal(0, appContext.KeyDownHandlerCount);

        coordinator.Dispose();

        Assert.Equal(0, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void InputCoordinatorDoubleDisposeIsIdempotent()
    {
        var appContext = new FakeTuiApplicationContext();
        using var window = new Window();
        using var prompt = new PromptEditor();

        var coordinator = new PiSharp.Tui.Interactive.Input.TuiInputCoordinator(
            window, new PiSharp.Tui.Interactive.Components.ChatView(),
            prompt, () => { },
            PiSharp.Tui.Interactive.TuiShortcutDispatcher.CreateDefaultAppDispatcher(),
            new PiSharp.Tui.Interactive.TuiShortcutContext(),
            new PiSharp.Tui.Interactive.TuiShortcutController(
                new PiSharp.Tui.Interactive.TuiShortcutControllerOptions(
                    () => [], null!, _ => { })),
            _ => { });

        coordinator.Attach();

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Equal(0, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void CtrlCShortcutClearsNonEmptyPromptBeforeExit()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            PiSharp.Abstractions.Options.ThinkingLevel.Off, null);
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        using var inlineSelection = new TuiInlineSelectionCoordinator(shell.Prompt, () => { }, appContext.Post);
        var sessionContext = new TuiSessionContext { CurrentRuntime = TuiIntegrationTestHost.CreateRuntimeFacade() };
        var stateGateway = new TuiStateGateway(() => state, next => state = next, renderCoordinator, appContext, CancellationToken.None);
        var options = new TuiHostOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), "sid", null, _ => Task.FromResult<string?>(null));
        var shortcutActions = new TuiShortcutActionHandler(
            shell, inlineSelection, appContext, stateGateway, sessionContext, options, CancellationToken.None);
        shell.Prompt.SetPromptText("draft");

        shortcutActions.HandleCtrlCShortcut();

        Assert.Equal(string.Empty, shell.Prompt.PromptText);
        Assert.Equal(0, appContext.StopRequestCount);
    }

    [Fact]
    public void CtrlCShortcutRequiresSecondPressToExitWhenPromptIsEmpty()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            PiSharp.Abstractions.Options.ThinkingLevel.Off, null);
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        using var inlineSelection = new TuiInlineSelectionCoordinator(shell.Prompt, () => { }, appContext.Post);
        var sessionContext = new TuiSessionContext { CurrentRuntime = TuiIntegrationTestHost.CreateRuntimeFacade() };
        var stateGateway = new TuiStateGateway(() => state, next => state = next, renderCoordinator, appContext, CancellationToken.None);
        var options = new TuiHostOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), "sid", null, _ => Task.FromResult<string?>(null));
        var shortcutActions = new TuiShortcutActionHandler(
            shell, inlineSelection, appContext, stateGateway, sessionContext, options, CancellationToken.None);

        shortcutActions.HandleCtrlCShortcut();
        appContext.Dispatcher.PumpPosted();
        Assert.Equal(0, appContext.StopRequestCount);

        shortcutActions.HandleCtrlCShortcut();
        appContext.Dispatcher.PumpPosted();

        Assert.Equal(1, appContext.StopRequestCount);
    }

    [Fact]
    public async Task HarnessSubscriptionLogsThinkingLevelBatchTransitions()
    {
        var harness = TuiIntegrationTestHost.CreateHarness();
        var runtime = TuiIntegrationTestHost.CreateRuntimeFacade(harness);
        var harnessId = RuntimeHelpers.GetHashCode(runtime);
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            ThinkingLevel.Off, null);
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        using var subscription = new TuiHarnessSubscription(
            () => runtime,
            () => state,
            next => state = next,
            _ => { },
            action => action(),
            resolveTool: null,
            loadSessionSnapshot: _ => Task.FromResult<TuiSessionSnapshot?>(null),
            applySessionSnapshot: (_, _) => { },
            loggerFactory: loggerFactory);

        subscription.Bind();

        Assert.Contains(provider.Entries, entry => entry.Category.Contains(nameof(TuiHarnessSubscription), StringComparison.Ordinal)
            && entry.Message.Contains($"harnessId={harnessId}", StringComparison.Ordinal));

        await harness.SetThinkingLevelAsync(ThinkingLevel.High);
        await WaitForLogAsync(() => provider.Entries.Any(entry => entry.Category.Contains(nameof(TuiHarnessSubscription), StringComparison.Ordinal)
            && entry.Message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && entry.Message.Contains("event=ThinkingLevelSelect", StringComparison.Ordinal)
            && entry.Message.Contains("enqueueResult=QueuedImmediately", StringComparison.Ordinal)));
        await WaitForLogAsync(() => provider.Entries.Any(entry => entry.Category.Contains(nameof(TuiHarnessSubscription), StringComparison.Ordinal)
            && entry.Message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && entry.Message.Contains("previousThinking=Off", StringComparison.Ordinal)
            && entry.Message.Contains("nextThinking=High", StringComparison.Ordinal)));

        Assert.Contains(provider.Entries, entry => entry.Category.Contains(nameof(TuiHarnessSubscription), StringComparison.Ordinal)
            && entry.Message.Contains("event=ThinkingLevelChanged", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, entry => entry.Category.Contains(nameof(TuiHarnessSubscription), StringComparison.Ordinal)
            && entry.Message.Contains("ThinkingLevelSelect:High", StringComparison.Ordinal)
            && entry.Message.Contains("ThinkingLevelChanged:High", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderCoordinatorLogsFooterSnapshotSummary()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            ThinkingLevel.Low, null) with
        {
            ModelDisplay = "test/m"
        };
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var shell = new TuiShellView(loggerFactory);
        using var coordinator = new TuiRenderCoordinator(
            shell,
            () => state,
            next => state = next,
            appContext,
            () => new TuiFooterSnapshot("repo", "main", 10, 20, 5, 35, 0.123m, 42d, 100, false, new Dictionary<string, string>()),
            loggerFactory: loggerFactory);

        coordinator.Render();

        Assert.Contains(provider.Entries, entry => entry.Category.Contains(nameof(TuiRenderCoordinator), StringComparison.Ordinal)
            && entry.Message.Contains("model=test/m", StringComparison.Ordinal)
            && entry.Message.Contains("thinking=Low", StringComparison.Ordinal)
            && entry.Message.Contains("branch=main", StringComparison.Ordinal)
            && entry.Message.Contains("totalTokens=35", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderCoordinatorPassesActiveToolsToFooter()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = TuiRenderState.Empty("sid", "file",
            new PiSharp.Agent.Core.Models.ModelDescriptor("test", "m", "test"),
            ThinkingLevel.Low, null);
        var shell = new TuiShellView();
        using var coordinator = new TuiRenderCoordinator(
            shell,
            () => state,
            next => state = next,
            appContext,
            () => new TuiFooterSnapshot("repo", "main", 10, 20, 5, 35, 0.123m, 42d, 100, false, new Dictionary<string, string>()),
            getActiveTools: () => ["read", "write"]);

        coordinator.Render();

        var footerText = shell.Footer.Text?.ToString() ?? string.Empty;
        Assert.Contains("tools:read,write", footerText);
    }


    [Fact]
    public async Task InlineSelectionCoordinatorDisposeCancelsActiveSession()
    {
        using var prompt = new PromptEditor();
        var coordinator = new PiSharp.Tui.Interactive.Sessions.TuiInlineSelectionCoordinator(
            prompt, () => { }, a => a());

        var task = coordinator.SelectInlineAsync("Test", ["a", "b"], CancellationToken.None);
        Assert.NotNull(coordinator.CurrentSession);

        coordinator.Dispose();

        Assert.True(task.IsCompleted);
        var result = await task;
        Assert.Null(result);
        Assert.Null(coordinator.CurrentSession);
    }

    [Fact]
    public void InlineSelectionCoordinatorDoubleDisposeIsIdempotent()
    {
        using var prompt = new PromptEditor();
        var coordinator = new PiSharp.Tui.Interactive.Sessions.TuiInlineSelectionCoordinator(
            prompt, () => { }, a => a());

        coordinator.SelectInlineAsync("Test", ["a", "b"], CancellationToken.None);
        Assert.NotNull(coordinator.CurrentSession);

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Null(coordinator.CurrentSession);
    }

    private static TuiFooterSnapshot EmptyFooterSnapshot()
        => new("", null, 0, 0, 0, 0, 0m, 0, 0, false, new Dictionary<string, string>());

    private static PiSharp.Tui.Interactive.Shell.TuiRenderCoordinator CreateRenderCoordinator(
        FakeTuiApplicationContext appContext,
        Func<PiSharp.Tui.Interactive.TuiRenderState> getState,
        Action<PiSharp.Tui.Interactive.TuiRenderState> setState)
    {
        var shellView = new PiSharp.Tui.Interactive.Shell.TuiShellView();
        return new PiSharp.Tui.Interactive.Shell.TuiRenderCoordinator(
            shellView, getState, setState, appContext,
            () => new PiSharp.Tui.Interactive.TuiFooterSnapshot(
                "/cwd", null, 0, 0, 0, 0, 0m, 0d, 0, false,
                new Dictionary<string, string>()));
    }

    private static async Task WaitForLogAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not met.");
            await Task.Delay(10);
        }
    }
}
