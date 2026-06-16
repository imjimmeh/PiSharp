using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using Terminal.Gui;

namespace PiSharp.Tui.Tests;

internal sealed class RecordingTerminalScreenSession : ITerminalScreenSession
{
    public int EnterCount { get; private set; }
    public int RestoreBracketedPasteCount { get; private set; }
    public int ExitCount { get; private set; }
    public int DisposeCount { get; private set; }

    public void Enter() => EnterCount++;

    public void RestoreBracketedPaste() => RestoreBracketedPasteCount++;

    public void Exit() => ExitCount++;

    public void Dispose() => DisposeCount++;
}

internal static class FakeDriverText
{
    public static string Extract(FakeDriver driver)
    {
        var contents = driver.Contents!;
        var rows = driver.Rows;
        var cols = driver.Cols;
        var lines = new List<string>(rows);
        for (var r = 0; r < rows; r++)
        {
            var sb = new StringBuilder(cols);
            for (var c = 0; c < cols; c++)
            {
                var rune = contents[r, c].Rune;
                sb.Append(rune.Value == 0 ? ' ' : rune.ToString());
            }
            lines.Add(sb.ToString().TrimEnd());
        }
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class RunningTuiHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultSubmitTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _runCancellation;
    private bool _stopped;

    public FakeDriver Driver { get; }
    public RecordingTerminalScreenSession Terminal { get; }
    public TuiHostRunContext Context { get; }
    public TuiProfilingCounters ProfilingCounters { get; }
    public Task<int> RunTask { get; }

    public string ScreenText => FakeDriverText.Extract(Driver);

    public RunningTuiHost(
        FakeDriver driver,
        RecordingTerminalScreenSession terminal,
        TuiHostRunContext context,
        TuiProfilingCounters profilingCounters,
        Task<int> runTask,
        CancellationTokenSource runCancellation)
    {
        Driver = driver;
        Terminal = terminal;
        Context = context;
        ProfilingCounters = profilingCounters;
        RunTask = runTask;
        _runCancellation = runCancellation;
    }

    public async Task<int> StopAsync(TimeSpan? timeout = null)
    {
        if (_stopped)
            return await RunTask;

        if (!RunTask.IsCompleted)
        {
            Exception? requestStopException = null;
            TryRequestStop(ref requestStopException);

            var stopTimeout = timeout ?? DefaultStopTimeout;
            var completed = await Task.WhenAny(RunTask, Task.Delay(stopTimeout));
            if (completed != RunTask)
            {
                _runCancellation.Cancel();
                TryRequestStop(ref requestStopException);
                completed = await Task.WhenAny(RunTask, Task.Delay(TimeSpan.FromSeconds(1)));
                if (completed != RunTask)
                {
                    throw new TimeoutException(
                        $"TUI host did not stop within {stopTimeout}. RunTask status: {RunTask.Status}.\nScreen:\n{ScreenText}",
                        requestStopException);
                }
            }
        }

        var result = await RunTask;
        _stopped = true;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopped)
            await StopAsync();

        _runCancellation.Dispose();
    }

    private void TryRequestStop(ref Exception? requestStopException)
    {
        try
        {
            Application.Invoke(() => Application.RequestStop(Context.Window));
        }
        catch (Exception ex)
        {
            requestStopException ??= ex;
        }
    }

    public async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + timeout.Value;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Condition not met within {timeout}.\nScreen:\n{ScreenText}");
            await Task.Delay(50, cancellationToken);
        }
    }

    public Task WaitUntilAsync(Func<string, bool> screenTextPredicate, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => WaitForAsync(() => screenTextPredicate(ScreenText), timeout, cancellationToken);

    public async Task<bool> TryUiThreadActionAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Application.Invoke(() =>
            {
                tcs.TrySetResult(true);
            });
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task SubmitPromptAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var submitTimeout = timeout ?? DefaultSubmitTimeout;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Application.Invoke(async () =>
            {
                try
                {
                    Context.Prompt.SetPromptText(text);
                    await Context.Prompt.SubmitAsync(cancellationToken);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        try
        {
            await tcs.Task.WaitAsync(submitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Prompt submission timed out after {submitTimeout}. Text: \"{text}\".\nScreen:\n{ScreenText}");
        }
    }

    public async Task SendPromptKeyAsync(Key key, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var submitTimeout = timeout ?? DefaultSubmitTimeout;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Application.Invoke(() =>
            {
                try
                {
                    Context.Prompt.NewKeyDownEvent(key);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        try
        {
            await tcs.Task.WaitAsync(submitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Prompt key dispatch timed out after {submitTimeout}. Key: {key}.\nScreen:\n{ScreenText}");
        }
    }

    public async Task SendApplicationKeyAsync(Key key, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var submitTimeout = timeout ?? DefaultSubmitTimeout;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Application.Invoke(() =>
            {
                try
                {
                    Application.RaiseKeyDownEvent(key);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        try
        {
            await tcs.Task.WaitAsync(submitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Application key dispatch timed out after {submitTimeout}. Key: {key}.\nScreen:\n{ScreenText}");
        }
    }
}

internal static class TuiIntegrationTestHost
{
    public static AgentHarness<JsonlSessionMetadata> CreateHarness(string assistantText = "ok")
    {
        var session = CreateSession();
        return new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            FakeStream(assistantText),
            FakeCompletion,
            []));
    }

    public static TuiHostOptions CreateOptions(
        AgentHarness<JsonlSessionMetadata> harness,
        RecordingTerminalScreenSession? terminalScreenSession = null,
        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>>? dispatchCommandAsync = null,
        Func<string, IReadOnlyList<string>>? completeCommand = null,
        Func<AgentHarness<JsonlSessionMetadata>>? getCurrentHarness = null,
        Func<CancellationToken, Task<TuiSessionSnapshot>>? getSessionSnapshotAsync = null,
        Func<IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>>>? getExtensionShortcuts = null,
        Func<CancellationToken, Task>? cycleThinkingLevelAsync = null,
        Func<string, string, CancellationToken, Task<(string Text, IReadOnlyList<ImageContent> Images)>>? processFileReferencesAsync = null,
        Func<string, IReadOnlyList<ImageContent>?, string, CancellationToken, Task<TuiInputHookResult>>? processInputAsync = null,
        Action<ExtensionUiBridgeHost>? configureUiBridge = null,
        Func<TuiExtensionLoadStatus>? getExtensionLoadStatus = null,
        IReadOnlySet<string>? commandWhitelist = null,
        TuiProfilingCounters? profilingCounters = null)
        => new(
            harness,
            SessionId: "test-session",
            SessionFile: null,
            GetSessionNameAsync: _ => Task.FromResult<string?>("Test session"),
            WorkingDirectory: Environment.CurrentDirectory,
            TerminalScreenSession: terminalScreenSession ?? new RecordingTerminalScreenSession(),
            DispatchCommandAsync: dispatchCommandAsync,
            CompleteCommand: completeCommand,
            GetCurrentHarness: getCurrentHarness,
            GetSessionSnapshotAsync: getSessionSnapshotAsync,
            GetExtensionShortcuts: getExtensionShortcuts,
            CycleThinkingLevelAsync: cycleThinkingLevelAsync,
            ProcessFileReferencesAsync: processFileReferencesAsync,
            ProcessInputAsync: processInputAsync,
            ConfigureUiBridge: configureUiBridge,
            GetExtensionLoadStatus: getExtensionLoadStatus,
            ExtensionLoadCommandWhitelist: commandWhitelist)
        {
            ProfilingCounters = profilingCounters
        };

    public static async Task<RunningTuiHost> StartAsync(
        FakeDriver? driver = null,
        RecordingTerminalScreenSession? terminal = null,
        AgentHarness<JsonlSessionMetadata>? harness = null,
        Func<TuiHostRunContext, CancellationToken, Task>? onBeforeRun = null,
        int width = 100,
        int height = 30,
        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>>? dispatchCommandAsync = null,
        Func<string, IReadOnlyList<string>>? completeCommand = null,
        Func<AgentHarness<JsonlSessionMetadata>>? getCurrentHarness = null,
        Func<CancellationToken, Task<TuiSessionSnapshot>>? getSessionSnapshotAsync = null,
        IReadOnlyList<string>? startupMessages = null,
        TimeSpan? transientSystemMessageLifetime = null,
        Func<IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>>>? getExtensionShortcuts = null,
        Func<CancellationToken, Task>? cycleThinkingLevelAsync = null,
        Func<string, string, CancellationToken, Task<(string Text, IReadOnlyList<ImageContent> Images)>>? processFileReferencesAsync = null,
        Func<string, IReadOnlyList<ImageContent>?, string, CancellationToken, Task<TuiInputHookResult>>? processInputAsync = null,
        Action<ExtensionUiBridgeHost>? configureUiBridge = null,
        Func<TuiExtensionLoadStatus>? getExtensionLoadStatus = null,
        IReadOnlySet<string>? commandWhitelist = null)
    {
        driver ??= new FakeDriver();
        driver.SetWindowSize(width, height);
        terminal ??= new RecordingTerminalScreenSession();
        var profilingCounters = new TuiProfilingCounters();

        harness ??= CreateHarness();
        var readyTcs = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        TuiHostRunContext? capturedContext = null;

        var options = CreateOptions(harness, terminal, dispatchCommandAsync, completeCommand, getCurrentHarness, getSessionSnapshotAsync, getExtensionShortcuts,
            cycleThinkingLevelAsync, processFileReferencesAsync, processInputAsync, configureUiBridge, getExtensionLoadStatus, commandWhitelist, profilingCounters) with
        {
            ConsoleDriver = driver,
            StartupMessages = startupMessages,
            TransientSystemMessageLifetime = transientSystemMessageLifetime,
            BeforeRunAsync = async (context, ct) =>
            {
                capturedContext = context;
                if (onBeforeRun is not null)
                    await onBeforeRun(context, ct);
                readyTcs.TrySetResult(context);
            }
        };

        var host = new TuiHost(options);
        var runCancellation = new CancellationTokenSource();
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        var readyTimeout = TimeSpan.FromSeconds(10);
        var completed = await Task.WhenAny(readyTcs.Task, runTask, Task.Delay(readyTimeout));

        if (completed == readyTcs.Task)
        {
            var context = await readyTcs.Task;
            return new RunningTuiHost(driver, terminal, context, profilingCounters, runTask, runCancellation);
        }

        runCancellation.Cancel();
        if (capturedContext is not null)
        {
            try
            {
                Application.Invoke(() => Application.RequestStop(capturedContext.Window));
            }
            catch
            {
            }
        }

        if (completed == runTask)
        {
            try
            {
                var exitCode = await runTask;
                throw new InvalidOperationException(
                    $"TUI host exited before signaling readiness with exit code {exitCode}.");
            }
            finally
            {
                runCancellation.Dispose();
            }
        }

        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));
        runCancellation.Dispose();
        throw new TimeoutException($"TUI host did not start within {readyTimeout}.");
    }

    internal static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(
            new JsonlSessionMetadata("test-session", DateTimeOffset.UtcNow, Environment.CurrentDirectory, "memory://session")));

    internal static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("summarized"));

    private static AgentStreamAsync FakeStream(string text)
    {
        async IAsyncEnumerable<AssistantMessageEvent> Stream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____)
        {
            var message = new AssistantMessage([new TextContent(text)], StopReason: "stop");
            yield return new AssistantMessageEvent.Start(message);
            await Task.Yield();
            yield return new AssistantMessageEvent.Done(message);
        }

        return Stream;
    }
}
