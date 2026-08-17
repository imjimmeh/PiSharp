using System.Threading;
using System.Text.Json;
using System.Runtime.CompilerServices;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

[Collection(TuiIntegrationTestCollection.Name)]
public sealed class TuiHostIntegrationTests
{
    [Fact]
    public async Task HostWiresPromptCompletionsThroughAsyncCompletionPath()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();

        Assert.NotNull(running.Context.Prompt.CompleteAsync);
        Assert.Null(running.Context.Prompt.Complete);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task RunAsyncReachesRunContextBeforeBlockedSessionNameLoads()
    {
        var sessionNameCanComplete = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runContextReached = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver();
        driver.SetWindowSize(100, 30);
        var terminal = new RecordingTerminalScreenSession();
        using var runCancellation = new CancellationTokenSource();

        var options = TuiIntegrationTestHost.CreateOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), terminal) with
        {
            ConsoleDriver = driver,
            // Render-before-metadata is only guaranteed on the deferred-startup (remote) path, so opt
            // into StartupAsync with a hook that completes without waiting on a real startup handshake.
            StartupAsync = _ => Task.FromResult(new TuiHostStartupResult(Theme: null, StartupMessages: [])),
            GetSessionNameAsync = cancellationToken => sessionNameCanComplete.Task.WaitAsync(cancellationToken),
            BeforeRunAsync = (context, _) =>
            {
                runContextReached.TrySetResult(context);
                return Task.CompletedTask;
            }
        };
        var host = new TuiHost(options);
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        try
        {
            // The host must reach its app-running context while the session-name load is still blocked.
            await runContextReached.Task.WaitAsync(TimeSpan.FromSeconds(1));

            // The app loop must be live and rendering the shell while session metadata stays unresolved.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (driver.Contents is null || driver.Contents.Length == 0)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not render while the session-name load was blocked.");
                await Task.Delay(25);
            }
        }
        finally
        {
            sessionNameCanComplete.TrySetResult(null);
            var runContext = await runContextReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Application.Invoke(() => Application.RequestStop(runContext.Window));
            runCancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StartupHookDisablesPromptUntilStartupSucceedsThenEnablesIt()
    {
        var startupCanComplete = new TaskCompletionSource<TuiHostStartupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyTcs = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver();
        driver.SetWindowSize(100, 30);
        var terminal = new RecordingTerminalScreenSession();
        using var runCancellation = new CancellationTokenSource();

        var options = TuiIntegrationTestHost.CreateOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), terminal) with
        {
            ConsoleDriver = driver,
            StartupAsync = cancellationToken => startupCanComplete.Task.WaitAsync(cancellationToken),
            BeforeRunAsync = (context, _) =>
            {
                readyTcs.TrySetResult(context);
                return Task.CompletedTask;
            }
        };
        var host = new TuiHost(options);
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        try
        {
            var runContext = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Once the app loop runs, the connecting status is pinned and the prompt is disabled
            // while the startup hook is still pending.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not disable the prompt while the startup hook was pending.");
                await Task.Delay(25);
            }
            Assert.Contains("Connecting to daemon…", runContext.GetState().Transcript.Select(item => item.Text));

            startupCanComplete.TrySetResult(new TuiHostStartupResult(Theme: null, StartupMessages: ["daemon ready"]));

            // Once startup and metadata hydration succeed, the prompt is re-enabled on the UI thread.
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not enable the prompt after startup succeeded.");
                await Task.Delay(25);
            }
            Assert.Contains("daemon ready", runContext.GetState().Transcript.Select(item => item.Text));
        }
        finally
        {
            var runContext = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Application.Invoke(() => Application.RequestStop(runContext.Window));
            runCancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DeferredStartupCompletesWhenExtensionLoadStatusPollBlocks()
    {
        var statusPollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStatusPoll = new TaskCompletionSource<TuiExtensionLoadStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runContextReached = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusReadCount = 0;
        var driver = new FakeDriver();
        driver.SetWindowSize(100, 30);
        var terminal = new RecordingTerminalScreenSession();
        using var runCancellation = new CancellationTokenSource();

        var options = TuiIntegrationTestHost.CreateOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), terminal) with
        {
            ConsoleDriver = driver,
            StartupAsync = async cancellationToken =>
            {
                await statusPollEntered.Task.WaitAsync(cancellationToken);
                return new TuiHostStartupResult(Theme: null, StartupMessages: []);
            },
            GetExtensionLoadStatus = () =>
            {
                if (Interlocked.Increment(ref statusReadCount) <= 2)
                    return new TuiExtensionLoadStatus(0, 0, 0, 0, 0);

                statusPollEntered.TrySetResult();
                return releaseStatusPoll.Task.GetAwaiter().GetResult();
            },
            BeforeRunAsync = (context, _) =>
            {
                runContextReached.TrySetResult(context);
                return Task.CompletedTask;
            }
        };
        var host = new TuiHost(options);
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        try
        {
            var runContext = await runContextReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await statusPollEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (!runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Deferred startup was blocked by extension load-status polling.");
                await Task.Delay(25);
            }
        }
        finally
        {
            releaseStatusPoll.TrySetResult(new TuiExtensionLoadStatus(0, 0, 0, 0, 0));
            var runContext = await runContextReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Application.Invoke(() => Application.RequestStop(runContext.Window));
            runCancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [Fact]
    public async Task DeferredStartupBeginsWhenOnlyConnectingPostIsDelivered()
    {
        var startupCanComplete = new TaskCompletionSource<TuiHostStartupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyTcs = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver();
        driver.SetWindowSize(100, 30);
        var terminal = new RecordingTerminalScreenSession();
        using var runCancellation = new CancellationTokenSource();

        var options = TuiIntegrationTestHost.CreateOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), terminal) with
        {
            ConsoleDriver = driver,
            // A long render-frame interval guarantees no deferred render can enqueue a
            // Post before the host queues its connecting and hydration posts, so the
            // second delivered Post is deterministically the hydration-launch post that
            // DropSecondPostApplicationContext is meant to drop.
            TimingOptions = new TuiTimingOptions(
                RenderFrameInterval: TimeSpan.FromMinutes(1),
                HarnessEventBatchInterval: TimeSpan.FromMinutes(1)),
            // The connecting post must be enough to start startup hydration: the second
            // posted callback (which currently launches hydration) is deliberately dropped.
            ApplicationContext = new DropSecondPostApplicationContext(),
            StartupAsync = cancellationToken => startupCanComplete.Task.WaitAsync(cancellationToken),
            BeforeRunAsync = (context, _) =>
            {
                readyTcs.TrySetResult(context);
                return Task.CompletedTask;
            }
        };
        var host = new TuiHost(options);
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        try
        {
            var runContext = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Only the connecting post is delivered: the prompt is disabled with the
            // connecting status pinned while the startup gate is still pending.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not disable the prompt while the connecting post was pending.");
                await Task.Delay(25);
            }
            Assert.Contains("Connecting to daemon…", runContext.GetState().Transcript.Select(item => item.Text));

            startupCanComplete.TrySetResult(new TuiHostStartupResult(Theme: null, StartupMessages: ["daemon ready"]));

            // Startup must begin even though only the connecting post was delivered, so
            // the prompt is re-enabled shortly after the startup gate completes.
            deadline = DateTime.UtcNow.AddSeconds(1);
            while (!runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not begin startup hydration when only the connecting post was delivered.");
                await Task.Delay(25);
            }
            Assert.Contains("daemon ready", runContext.GetState().Transcript.Select(item => item.Text));
        }
        finally
        {
            var runContext = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Application.Invoke(() => Application.RequestStop(runContext.Window));
            runCancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StartupAsyncFaultLeavesShellRenderedWithPersistentActionableErrorAndDisabledPrompt()
    {
        var runContextReached = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver();
        driver.SetWindowSize(100, 30);
        var terminal = new RecordingTerminalScreenSession();
        using var runCancellation = new CancellationTokenSource();

        var options = TuiIntegrationTestHost.CreateOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), terminal) with
        {
            ConsoleDriver = driver,
            StartupAsync = _ => Task.FromException<TuiHostStartupResult>(
                new InvalidOperationException("daemon refused to start")),
            BeforeRunAsync = (context, _) =>
            {
                runContextReached.TrySetResult(context);
                return Task.CompletedTask;
            }
        };
        var host = new TuiHost(options);
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        try
        {
            var runContext = await runContextReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The app loop is live with the connecting status pinned and the prompt disabled
            // while the faulted startup hook is still in flight.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not disable the prompt while the startup hook was pending.");
                await Task.Delay(25);
            }
            Assert.Contains("Connecting to daemon…", runContext.GetState().Transcript.Select(item => item.Text));

            // The startup fault must surface as a persistent, actionable daemon error row
            // while the host stays rendered with input disabled.
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!runContext.GetState().Transcript.Any(item =>
                item.IsError && item.Text.Contains("Failed to connect to the daemon", StringComparison.Ordinal)))
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not surface the daemon startup error.");
                await Task.Delay(25);
            }

            var errorItem = Assert.Single(runContext.GetState().Transcript.Where(item =>
                item.IsError && item.Text.Contains("Failed to connect to the daemon", StringComparison.Ordinal)));
            Assert.Contains("daemon refused to start", errorItem.Text);
            Assert.Contains("Verify the daemon is running and restart the interactive session.", errorItem.Text);
            Assert.Null(errorItem.ExpiresAt);
            Assert.False(runContext.Prompt.Enabled);
            Assert.False(runTask.IsCompleted);
        }
        finally
        {
            var runContext = await runContextReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Application.Invoke(() => Application.RequestStop(runContext.Window));
            runCancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [Fact]
    public async Task TypingLandsInPromptAfterDeferredStartupCompletes()
    {
        var startupCanComplete = new TaskCompletionSource<TuiHostStartupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyTcs = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver();
        driver.SetWindowSize(100, 30);
        var terminal = new RecordingTerminalScreenSession();
        using var runCancellation = new CancellationTokenSource();

        var options = TuiIntegrationTestHost.CreateOptions(TuiIntegrationTestHost.CreateRuntimeFacade(), terminal) with
        {
            ConsoleDriver = driver,
            StartupAsync = cancellationToken => startupCanComplete.Task.WaitAsync(cancellationToken),
            BeforeRunAsync = (context, _) =>
            {
                readyTcs.TrySetResult(context);
                return Task.CompletedTask;
            }
        };
        var host = new TuiHost(options);
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        try
        {
            var runContext = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The prompt is disabled while the deferred startup (daemon connect) is pending.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not disable the prompt while the startup hook was pending.");
                await Task.Delay(25);
            }

            startupCanComplete.TrySetResult(new TuiHostStartupResult(Theme: null, StartupMessages: ["daemon ready"]));

            // Startup succeeds: the prompt is re-enabled (the host reports ready).
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!runContext.Prompt.Enabled)
            {
                if (runTask.IsCompleted || DateTime.UtcNow >= deadline)
                    throw new TimeoutException("TUI host did not enable the prompt after startup succeeded.");
                await Task.Delay(25);
            }

            // Typing must reach the prompt once the host is ready.
            var keySent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Application.Invoke(() =>
            {
                try
                {
                    var handled = Application.RaiseKeyDownEvent(new Key((KeyCode)'h'));
                    keySent.TrySetResult(handled);
                }
                catch (Exception ex)
                {
                    keySent.TrySetException(ex);
                }
            });
            var handled = await keySent.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(handled, "Typed key was not handled by the TUI after startup completed.");
            Assert.Contains("h", runContext.Prompt.PromptText);
        }
        finally
        {
            var runContext = await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Application.Invoke(() => Application.RequestStop(runContext.Window));
            runCancellation.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SessionCommandLoadingShowsThenRemovesLoadingRow()
    {
        var dispatchTcs = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            dispatchStartedTcs.TrySetResult();
            return await dispatchTcs.Task.WaitAsync(ct);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);

        await running.SubmitPromptAsync("/session");

        await dispatchStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await running.WaitForAsync(() =>
        {
            var state = running.Context.GetState();
            return state.Transcript.Any(item => item.Text.Contains("Loading sessions", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(5));

        dispatchTcs.TrySetResult(new TuiCommandDispatchResult(true));

        // After TrySetResult there are two thread-pool hops before EndCommandLoading
        // sets state; under heavy suite load those hops can exceed 5 s, so budget
        // well beyond the loading row's own 15 s safety-net expiry.
        await running.WaitForAsync(() =>
        {
            var state = running.Context.GetState();
            return !state.Transcript.Any(item => item.Text.Contains("Loading sessions", StringComparison.Ordinal))
                && state.Status == "Idle"
                && !state.IsBusy;
        }, TimeSpan.FromSeconds(30));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task RunAsyncWithFakeDriverRendersStartupAndCleansUpTerminalSession()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        await running.WaitUntilAsync(text => text.Length > 0);

        var screenText = running.ScreenText;
        var result = await running.StopAsync();

        Assert.Equal(0, result);
        Assert.NotNull(running.Context);
        Assert.Equal(1, running.Terminal.EnterCount);
        Assert.Equal(1, running.Terminal.RestoreBracketedPasteCount);
        Assert.Equal(1, running.Terminal.ExitCount);
        Assert.NotEmpty(screenText);
    }

    [Fact]
    public async Task CustomUiReceivesAlreadyHandledApplicationKeyThroughGlobalRouter()
    {
        ExtensionUiBridgeHost? bridge = null;
        var forwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var running = await TuiIntegrationTestHost.StartAsync(configureUiBridge: configuredBridge =>
        {
            bridge = configuredBridge;
            configuredBridge.SendCustomUiInputAsync = (requestId, data, width, height, _, _) =>
            {
                forwarded.TrySetResult(data ?? string.Empty);
                return Task.FromResult(new ExtensionCustomUiSnapshot(
                    requestId,
                    ["Done"],
                    width ?? 80,
                    height ?? 24,
                    Completed: true,
                    Value: data));
            };
        });
        Assert.NotNull(bridge);

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);
        var showTask = bridge.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await running.WaitForAsync(() => bridge.HasActiveCustomUi, TimeSpan.FromSeconds(5));

        var enter = new Key(KeyCode.Enter) { Handled = true };
        await running.SendApplicationKeyAsync(enter);

        Assert.Equal("\r", await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(enter.Handled);

        var result = await showTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Ok);
        Assert.Equal("\r", Assert.IsType<string>(result.Value));
    }

    [Fact]
    public async Task SubmitPromptAsyncSendsPromptThroughHarnessAndRendersAssistantResponse()
    {
        var promptText = "hello from integration";
        var assistantText = "assistant integration reply";
        var harness = TuiIntegrationTestHost.CreateHarness(assistantText);
        await using var running = await TuiIntegrationTestHost.StartAsync(runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harness));

        await running.SubmitPromptAsync(promptText);

        var renderedText = string.Empty;
        await running.WaitUntilAsync(text =>
        {
            renderedText = text;
            return text.Contains(assistantText);
        });

        Assert.Contains(assistantText, renderedText);

        var session = harness.Session;
        var context = await session.BuildContextAsync();
        var userMessages = context.Messages.OfType<UserMessage>().ToArray();
        var assistantMessages = context.Messages.OfType<AssistantMessage>().ToArray();
        Assert.Contains(userMessages, m => m.Content.OfType<TextContent>().Any(c => c.Text.Contains(promptText)));
        Assert.Contains(assistantMessages, m => m.Content.OfType<TextContent>().Any(c => c.Text.Contains(assistantText)));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task EscapeDuringRunningAgentRequestClearsBusyStateAndWorkingIndicator()
    {
        var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AssistantMessageEvent> BlockingStream(
            ModelDescriptor _,
            AgentContext __,
            AgentStreamOptions ___,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            streamStarted.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        var session = TuiIntegrationTestHost.CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            BlockingStream,
            TuiIntegrationTestHost.FakeCompletion,
            []));
        await using var running = await TuiIntegrationTestHost.StartAsync(runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harness));

        await running.SubmitPromptAsync("cancel this request");
        await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await running.WaitForAsync(() => running.Context.GetState().IsBusy, TimeSpan.FromSeconds(5));
        await running.WaitUntilAsync(text => text.Contains("Working...", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await running.SendApplicationKeyAsync(Key.Esc);

        await running.WaitForAsync(() =>
        {
            var state = running.Context.GetState();
            return !state.IsBusy
                && state.Status == "Idle"
                && !running.ScreenText.Contains("Working...", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task SubmitPromptAsyncContinuesAfterAsyncInputHookWhenCallerSynchronizationContextDoesNotPump()
    {
        var promptText = "context-sensitive submit";
        var assistantText = "context-sensitive reply";
        var inputHookStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputHookResult = new TaskCompletionSource<TuiInputHookResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<string, IReadOnlyList<ImageContent>?, string, CancellationToken, Task<TuiInputHookResult>> processInputAsync = (text, _, _, _) =>
        {
            Assert.Equal(promptText, text);
            inputHookStarted.TrySetResult();
            return inputHookResult.Task;
        };

        var harness = TuiIntegrationTestHost.CreateHarness(assistantText);
        await using var running = await TuiIntegrationTestHost.StartAsync(
            runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harness), processInputAsync: processInputAsync);
        var submitStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        Application.Invoke(() =>
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                running.Context.Prompt.SetPromptText(promptText);
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
                submitStarted.TrySetResult(running.Context.Prompt.SubmitAsync());
            }
            catch (Exception ex)
            {
                submitStarted.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });

        var submitTask = await submitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await inputHookStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        inputHookResult.TrySetResult(new TuiInputHookResult(false, promptText, null));

        await running.WaitUntilAsync(text => text.Contains(assistantText, StringComparison.Ordinal));
        await submitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running.Context.Prompt.IsSubmitting);

        var sessionContext = await harness.Session.BuildContextAsync();
        var userMessages = sessionContext.Messages.OfType<UserMessage>().ToArray();
        Assert.Contains(userMessages, message => message.Content.OfType<TextContent>().Any(content => content.Text.Contains(promptText, StringComparison.Ordinal)));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task SubmitPromptAsyncRestoresPromptAfterAsyncInputHookFailureWhenCallerSynchronizationContextDoesNotPump()
    {
        var promptText = "context-sensitive failure";
        var inputHookStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputHookCanFail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<TuiInputHookResult> ProcessInputAsync(string text, IReadOnlyList<ImageContent>? _, string __, CancellationToken ___)
        {
            Assert.Equal(promptText, text);
            inputHookStarted.TrySetResult();
            await inputHookCanFail.Task.ConfigureAwait(false);
            throw new InvalidOperationException("async input hook failed");
        }

        await using var running = await TuiIntegrationTestHost.StartAsync(processInputAsync: ProcessInputAsync);
        var submitStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        Application.Invoke(() =>
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                running.Context.Prompt.SetPromptText(promptText);
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
                submitStarted.TrySetResult(running.Context.Prompt.SubmitAsync());
            }
            catch (Exception ex)
            {
                submitStarted.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });

        var submitTask = await submitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await inputHookStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        inputHookCanFail.TrySetResult();

        await submitTask.WaitAsync(TimeSpan.FromSeconds(5));
        await running.WaitForAsync(() =>
        {
            var state = running.Context.GetState();
            return state.Transcript.Any(item =>
                item.Role == "system" && item.Text.Contains("async input hook failed", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(5));
        Assert.Equal(promptText, running.Context.Prompt.PromptText);
        Assert.False(running.Context.Prompt.IsSubmitting);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExtensionUiRequestRoutingRoutesSupportedKinds()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var editorText = "current editor text";
        var notifications = new List<string>();
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), () => editorText, text => editorText = text)
        {
            DispatchUi = action => action(),
            ShowNotification = message => notifications.Add(message)
        };
        IExtensionUi ui = new TuiExtensionUi(host);

        var select = await ui.RequestAsync(new ExtensionUiRequest("ext", "select", JsonDocument.Parse("""
        {"message":"Pick one","options":["Alpha","Beta"]}
        """).RootElement.Clone()));
        Assert.True(select.Ok);
        Assert.Equal("Alpha", select.Value);

        var prompt = await ui.RequestAsync(new ExtensionUiRequest("ext", "prompt", JsonDocument.Parse("""
        {"message":"Enter a value","initialValue":"seed"}
        """).RootElement.Clone()));
        Assert.True(prompt.Ok);
        Assert.Equal("seed", prompt.Value);

        var confirm = await ui.RequestAsync(new ExtensionUiRequest("ext", "confirm", JsonDocument.Parse("""
        {"message":"Continue?"}
        """).RootElement.Clone()));
        Assert.True(confirm.Ok);
        Assert.True((bool)Assert.IsType<bool>(confirm.Value));

        var markdown = await ui.RequestAsync(new ExtensionUiRequest("ext", "markdown", JsonDocument.Parse("""
        {"markdown":"# Heading"}
        """).RootElement.Clone()));
        Assert.True(markdown.Ok);
        Assert.Contains("# Heading", notifications);

        var editorGetText = await ui.RequestAsync(new ExtensionUiRequest("ext", "editor_get_text", JsonDocument.Parse("{}").RootElement.Clone()));
        Assert.True(editorGetText.Ok);
        Assert.Equal("current editor text", editorGetText.Value);

        var editorSetText = await ui.RequestAsync(new ExtensionUiRequest("ext", "editor_set_text", JsonDocument.Parse("""
        {"text":"updated editor text"}
        """).RootElement.Clone()));
        Assert.True(editorSetText.Ok);
        Assert.Equal("updated editor text", editorText);

        var workingMessage = await ui.RequestAsync(new ExtensionUiRequest("ext", "working_message", JsonDocument.Parse("""
        {"message":"Crunching"}
        """).RootElement.Clone()));
        Assert.True(workingMessage.Ok);
        Assert.Equal("Crunching", state.WorkingMessage);

        var title = await ui.RequestAsync(new ExtensionUiRequest("ext", "title", JsonDocument.Parse("""
        {"title":"Bridge title"}
        """).RootElement.Clone()));
        Assert.True(title.Ok);
        Assert.Equal("Bridge title", state.TitleOverride);

        var workingVisible = await ui.RequestAsync(new ExtensionUiRequest("ext", "working_visible", JsonDocument.Parse("""
        {"visible":false}
        """).RootElement.Clone()));
        Assert.True(workingVisible.Ok);
        Assert.False(state.WorkingVisible);

        var workingIndicator = await ui.RequestAsync(new ExtensionUiRequest("ext", "working_indicator", JsonDocument.Parse("""
        {"indicator":{"message":"Loading","visible":true,"spinner":"●"}}
        """).RootElement.Clone()));
        Assert.True(workingIndicator.Ok);
        Assert.Equal("Loading", state.WorkingIndicator?.Message);
        Assert.True(state.WorkingIndicator?.Visible == true);
        Assert.Equal("●", state.WorkingIndicator?.Spinner);

        var workingIndicatorHidden = await ui.RequestAsync(new ExtensionUiRequest("ext", "working_indicator", JsonDocument.Parse("""
        {"indicator":{"message":"Hidden","visible":false,"spinner":"○"}}
        """).RootElement.Clone()));
        Assert.True(workingIndicatorHidden.Ok);
        Assert.Equal("Hidden", state.WorkingIndicator?.Message);
        Assert.Equal(false, state.WorkingIndicator?.Visible);
        Assert.Equal("○", state.WorkingIndicator?.Spinner);
    }

    [Fact]
    public async Task HelpCommandSubmittedThroughHostRendersBuiltInHelp()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();

        await running.SubmitPromptAsync("/help");

        var state = running.Context.GetState();
        var helpItem = Assert.Single(state.Transcript,
            item => item.Role == "system" && item.Text.Contains("Commands:"));
        Assert.Contains("Commands: /abort, /exit, /model, /session, /clear", helpItem.Text);

        await running.StopAsync();
    }

    [Fact]
    public async Task ClearCommandSubmittedThroughHostClearsTranscript()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();

        await running.SubmitPromptAsync("/help");
        var stateAfterHelp = running.Context.GetState();
        Assert.NotEmpty(stateAfterHelp.Transcript);

        await running.SubmitPromptAsync("/clear");

        var stateAfterClear = running.Context.GetState();
        Assert.Empty(stateAfterClear.Transcript);

        await running.StopAsync();
    }

    [Fact]
    public async Task ExitCommandSubmittedThroughHostStopsCleanly()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        await running.WaitUntilAsync(text => text.Length > 0);

        await running.SubmitPromptAsync("/exit");

        var completed = await Task.WhenAny(running.RunTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(running.RunTask, completed);
        var exitCode = await running.RunTask;
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AsciiCtrlCThroughFocusedPromptClearsThenExitsOnSecondPress()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        await running.WaitUntilAsync(text => text.Length > 0);
        running.Context.Prompt.SetPromptText("draft");

        await running.SendPromptKeyAsync(new Key((KeyCode)3));

        Assert.Equal(string.Empty, running.Context.Prompt.PromptText);
        Assert.False(running.RunTask.IsCompleted);

        await running.SendPromptKeyAsync(new Key((KeyCode)3));

        var completed = await Task.WhenAny(running.RunTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(running.RunTask, completed);
        var exitCode = await running.RunTask;
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task CtrlYFromPromptFocusDispatchesThinkingLevelShortcut()
    {
        var cycleThinkingLevelCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var running = await TuiIntegrationTestHost.StartAsync(onBeforeRun: (context, _) =>
        {
            context.Prompt.FocusAtEnd();
            return Task.CompletedTask;
        }, cycleThinkingLevelAsync: _ =>
        {
            cycleThinkingLevelCalled.TrySetResult();
            return Task.CompletedTask;
        });

        await running.WaitUntilAsync(text => text.Length > 0);

        await running.SendPromptKeyAsync(Key.Y.WithCtrl);

        await cycleThinkingLevelCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task CtrlYBeforeFirstPromptCyclesVisibleThinkingLevelForReasoningModel()
    {
        var model = new ModelDescriptor(
            "test",
            "reasoning",
            "test",
            Reasoning: true,
            ThinkingLevelMap: new Dictionary<string, int>
            {
                ["low"] = 1,
                ["high"] = 2
            });

        async IAsyncEnumerable<AssistantMessageEvent> Stream(
            ModelDescriptor _,
            AgentContext __,
            AgentStreamOptions ___,
            [EnumeratorCancellation] CancellationToken ____)
        {
            var message = new AssistantMessage([new TextContent("ok")], StopReason: "stop");
            yield return new AssistantMessageEvent.Start(message);
            await Task.Yield();
            yield return new AssistantMessageEvent.Done(message);
        }

        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            TuiIntegrationTestHost.CreateSession(),
            model,
            Stream,
            TuiIntegrationTestHost.FakeCompletion,
            []));

        await using var running = await TuiIntegrationTestHost.StartAsync(
            runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harness),
            onBeforeRun: (context, _) =>
            {
                context.Prompt.FocusAtEnd();
                return Task.CompletedTask;
            },
            cycleThinkingLevelAsync: token => harness.SetThinkingLevelAsync(ThinkingLevel.Low, token));

        await running.WaitUntilAsync(text => text.Contains("thinking off", StringComparison.OrdinalIgnoreCase));

        await running.SendPromptKeyAsync(Key.Y.WithCtrl);

        await running.WaitUntilAsync(text => text.Contains("thinking low", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ThinkingLevel.Low, running.Context.GetState().ThinkingLevel);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task HomeWithEmptyPromptScrollsTranscriptToTop()
    {
        var startupMessages = Enumerable.Range(1, 30).Select(i => $"line {i}").ToArray();

        await using var running = await TuiIntegrationTestHost.StartAsync(startupMessages: startupMessages, height: 12);
        await running.WaitForAsync(() => running.Context.Chat.ScrollTop > 0);

        await running.SendApplicationKeyAsync(Key.Home);

        Assert.Equal(0, running.Context.Chat.ScrollTop);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task EndWithNonEmptyPromptDoesNotScrollTranscript()
    {
        var startupMessages = Enumerable.Range(1, 30).Select(i => $"line {i}").ToArray();

        await using var running = await TuiIntegrationTestHost.StartAsync(startupMessages: startupMessages, height: 12);
        await running.WaitForAsync(() => running.Context.Chat.ScrollTop > 0);
        await running.SendApplicationKeyAsync(Key.Home);
        Assert.Equal(0, running.Context.Chat.ScrollTop);
        running.Context.Prompt.SetPromptText("draft");

        await running.SendApplicationKeyAsync(Key.End);

        Assert.Equal(0, running.Context.Chat.ScrollTop);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task AbortCommandSubmittedThroughHostAppendsAbortRequested()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();

        await running.SubmitPromptAsync("/abort");

        var state = running.Context.GetState();
        Assert.Contains(state.Transcript,
            item => item.Role == "system" && item.Text.Contains("Abort requested."));

        await running.StopAsync();
    }

    [Fact]
    public async Task DispatchCommandSubmittedThroughHostAllowsConcurrentCommandDispatch()
    {
        var dispatchTcs = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDispatchTcs = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        string? firstDispatched = null;
        string? secondDispatched = null;

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            var count = Interlocked.Increment(ref dispatchCount);
            if (count == 1)
            {
                firstDispatched = request.Text;
                dispatchStartedTcs.TrySetResult();
                return await dispatchTcs.Task.WaitAsync(ct);
            }
            secondDispatched = request.Text;
            return await secondDispatchTcs.Task.WaitAsync(ct);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);

        await running.SubmitPromptAsync("/model");
        await dispatchStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await running.SubmitPromptAsync("/session");

        await Task.Delay(300);

        Assert.Equal(2, dispatchCount);
        Assert.Equal("/model", firstDispatched);
        Assert.Equal("/session", secondDispatched);

        dispatchTcs.TrySetResult(new TuiCommandDispatchResult(true));
        secondDispatchTcs.TrySetResult(new TuiCommandDispatchResult(true));

        await running.WaitForAsync(() => !running.Context.GetState().IsBusy, TimeSpan.FromSeconds(5));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task InlineSelectionSubmitCompletesWhileCommandIsInProgress()
    {
        var selectedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            var selected = await request.SelectAsync("Select model", ["openai/gpt-4o", "anthropic/claude-sonnet"], ct);
            selectedTcs.TrySetResult(selected);
            return new TuiCommandDispatchResult(true);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);

        await running.SubmitPromptAsync("/model");
        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await running.SubmitPromptAsync("openai/gpt-4o");

        var selectedModel = await selectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("openai/gpt-4o", selectedModel);
        await running.WaitUntilAsync(text => !text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task InlineSelectionArrowKeysThenEnterSelectsCorrectOption()
    {
        var selectedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            var selected = await request.SelectAsync("Select model", ["openai/gpt-4o", "anthropic/claude-sonnet"], ct);
            selectedTcs.TrySetResult(selected);
            return new TuiCommandDispatchResult(true);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);

        await running.SubmitPromptAsync("/model");
        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await running.SendPromptKeyAsync(Key.CursorDown);
        await Task.Delay(200);
        await running.SendPromptKeyAsync(Key.Enter);

        var selectedModel = await selectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("anthropic/claude-sonnet", selectedModel);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task InlineSelectionApplicationArrowKeysThenEnterSelectsCorrectOption()
    {
        var selectedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            var selected = await request.SelectAsync("Select model", ["openai/gpt-4o", "anthropic/claude-sonnet"], ct);
            selectedTcs.TrySetResult(selected);
            return new TuiCommandDispatchResult(true);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);

        await running.SubmitPromptAsync("/model");
        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await running.SendApplicationKeyAsync(Key.CursorDown);
        await running.WaitUntilAsync(text => text.Contains("→ anthropic/claude-sonnet", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await running.SendApplicationKeyAsync(Key.Enter);

        var selectedModel = await selectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("anthropic/claude-sonnet", selectedModel);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task InlineSelectionReusedApplicationArrowKeyKeepsNavigatingOptions()
    {
        var selectedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            var selected = await request.SelectAsync("Select model", ["openai/gpt-4o", "anthropic/claude-sonnet"], ct);
            selectedTcs.TrySetResult(selected);
            return new TuiCommandDispatchResult(true);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);

        await running.SubmitPromptAsync("/model");
        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        var down = Key.CursorDown;
        await running.SendApplicationKeyAsync(down);
        await running.WaitUntilAsync(text => text.Contains("→ anthropic/claude-sonnet", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await running.SendApplicationKeyAsync(down);
        await running.WaitUntilAsync(text => text.Contains("→ openai/gpt-4o", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await running.SendApplicationKeyAsync(Key.Enter);

        var selectedModel = await selectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("openai/gpt-4o", selectedModel);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task InlineSelectionCommandDispatchContinuesWhenCallerSynchronizationContextDoesNotPump()
    {
        var selectedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            var selected = await request.SelectAsync("Select model", ["openai/gpt-4o", "anthropic/claude-sonnet"], ct);
            selectedTcs.TrySetResult(selected);
            return new TuiCommandDispatchResult(true);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);
        var submitStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        Application.Invoke(() =>
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                running.Context.Prompt.SetPromptText("/model");
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
                submitStarted.TrySetResult(running.Context.Prompt.SubmitAsync());
            }
            catch (Exception ex)
            {
                submitStarted.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });

        var submitTask = await submitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await submitTask.WaitAsync(TimeSpan.FromSeconds(5));
        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await running.SendApplicationKeyAsync(Key.Enter);

        var selectedModel = await selectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("openai/gpt-4o", selectedModel);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task InlineSelectionEscCompletesCommandAndAllowsSecondModelPicker()
    {
        var selections = new List<string?>();

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            var selected = await request.SelectAsync("Select model", ["openai/gpt-4o", "anthropic/claude-sonnet"], ct);
            lock (selections)
            {
                selections.Add(selected);
            }

            return new TuiCommandDispatchResult(true);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(dispatchCommandAsync: dispatchCommandAsync);

        await running.SubmitPromptAsync("/model");
        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        await running.SendApplicationKeyAsync(Key.Esc);
        await running.WaitUntilAsync(text => !text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await running.WaitForAsync(() =>
        {
            lock (selections)
            {
                return selections.Count == 1 && selections[0] is null;
            }
        }, TimeSpan.FromSeconds(5));

        await running.SubmitPromptAsync("/model");
        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await running.SendApplicationKeyAsync(Key.Enter);

        await running.WaitForAsync(() =>
        {
            lock (selections)
            {
                return selections.Count == 2 && selections[1] == "openai/gpt-4o";
            }
        }, TimeSpan.FromSeconds(5));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task SessionRefreshAfterDispatchUpdatesStateWithNewSessionInfo()
    {
        var dispatchTcs = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var snapshotCallCount = 0;

        Func<CancellationToken, Task<TuiSessionSnapshot>> getSessionSnapshotAsync = _ =>
        {
            var count = Interlocked.Increment(ref snapshotCallCount);
            return Task.FromResult(count == 1
                ? new TuiSessionSnapshot("test-session", null, "Test session", [])
                : new TuiSessionSnapshot("new-session", "new-session.jsonl", "New Session", []));
        };

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = async (request, ct) =>
        {
            dispatchStartedTcs.TrySetResult();
            return await dispatchTcs.Task.WaitAsync(ct);
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(
            dispatchCommandAsync: dispatchCommandAsync,
            getSessionSnapshotAsync: getSessionSnapshotAsync);

        Assert.Equal("test-session", running.Context.GetState().SessionId);
        Assert.Equal("Test session", running.Context.GetState().SessionName);

        await running.SubmitPromptAsync("/resume");

        await dispatchStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        dispatchTcs.TrySetResult(new TuiCommandDispatchResult(true));

        await running.WaitForAsync(() =>
            running.Context.GetState().SessionId == "new-session"
            && running.Context.GetState().SessionName == "New Session", TimeSpan.FromSeconds(5));

        Assert.Equal("new-session", running.Context.GetState().SessionId);
        Assert.Equal("New Session", running.Context.GetState().SessionName);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task TransientStartupMessagesExpireAfterLifetimeElapsesInHostLoop()
    {
        var startupMessage = "Temporary startup notification";
        var lifetime = TimeSpan.FromMilliseconds(500);
        var waitTimeout = TimeSpan.FromSeconds(5);

        await using var running = await TuiIntegrationTestHost.StartAsync(
            startupMessages: new[] { startupMessage },
            transientSystemMessageLifetime: lifetime);

        var initialState = running.Context.GetState();
        Assert.Contains(initialState.Transcript,
            item => item.Role == "system" && item.Text.Contains(startupMessage));

        await running.WaitForAsync(() =>
        {
            var state = running.Context.GetState();
            return !state.Transcript.Any(
                item => item.Role == "system" && item.Text.Contains(startupMessage));
        }, waitTimeout);

        var finalState = running.Context.GetState();
        Assert.DoesNotContain(finalState.Transcript,
            item => item.Role == "system" && item.Text.Contains(startupMessage));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ProcessFileReferencesAsyncReceivesPromptTextAndDelegatesToHook()
    {
        string? capturedHookText = null;
        string? capturedHookWorkingDirectory = null;
        var hookTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var filePath = "source/file.cs";
        var processedPrefix = "[refs-expanded] ";

        Func<string, string, CancellationToken, Task<(string Text, IReadOnlyList<ImageContent> Images)>> processFileRefsAsync = (text, cwd, ct) =>
        {
            capturedHookText = text;
            capturedHookWorkingDirectory = cwd;
            hookTcs.TrySetResult();
            return Task.FromResult((processedPrefix + text, (IReadOnlyList<ImageContent>)Array.Empty<ImageContent>()));
        };

        var harne = TuiIntegrationTestHost.CreateHarness("reply");
        await using var running = await TuiIntegrationTestHost.StartAsync(
            runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harne), processFileReferencesAsync: processFileRefsAsync);

        await running.SubmitPromptAsync(filePath);
        await hookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(filePath, capturedHookText);
        Assert.Equal(Environment.CurrentDirectory, capturedHookWorkingDirectory);

        var expectedProcessedText = processedPrefix + filePath;

        await running.WaitUntilAsync(text => text.Contains("reply"));

        var session = harne.Session;
        var context = await session.BuildContextAsync();
        var userMessages = context.Messages.OfType<UserMessage>().ToArray();
        Assert.Contains(userMessages, m => m.Content.OfType<TextContent>().Any(c => c.Text.Contains(expectedProcessedText)));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ProcessInputAsyncReceivesRawPromptTextAndAllowsTransformationHook()
    {
        string? capturedOriginalText = null;
        var hookTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transformedSuffix = " [transformed]";

        Func<string, IReadOnlyList<ImageContent>?, string, CancellationToken, Task<TuiInputHookResult>> processInputAsync = (text, images, source, ct) =>
        {
            capturedOriginalText = text;
            hookTcs.TrySetResult();
            return Task.FromResult(new TuiInputHookResult(false, text + transformedSuffix, null));
        };

        var harne = TuiIntegrationTestHost.CreateHarness("ok");
        await using var running = await TuiIntegrationTestHost.StartAsync(
            runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harne), processInputAsync: processInputAsync);

        var originalText = "hello world";
        await running.SubmitPromptAsync(originalText);
        await hookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(originalText, capturedOriginalText);

        var expectedTransformed = originalText + transformedSuffix;

        await running.WaitUntilAsync(text => text.Contains("ok"));

        var session = harne.Session;
        var context = await session.BuildContextAsync();
        var userMessages = context.Messages.OfType<UserMessage>().ToArray();
        Assert.Contains(userMessages, m => m.Content.OfType<TextContent>().Any(c => c.Text.Contains(expectedTransformed)));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ProcessInputAsyncHandledTrueLogsInfoAndSkipsRuntimePromptDispatch()
    {
        var hookTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<string, IReadOnlyList<ImageContent>?, string, CancellationToken, Task<TuiInputHookResult>> processInputAsync = (text, images, source, ct) =>
        {
            hookTcs.TrySetResult();
            return Task.FromResult(new TuiInputHookResult(Handled: true, Text: text, Images: null));
        };

        var harne = TuiIntegrationTestHost.CreateHarness("ok");
        await using var running = await TuiIntegrationTestHost.StartAsync(
            runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harne), processInputAsync: processInputAsync);

        var promptText = "handled by extension";
        await running.SubmitPromptAsync(promptText);
        await hookTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await running.WaitUntilAsync(text => !text.Contains(promptText));

        var context = await harne.Session.BuildContextAsync();
        var userMessages = context.Messages.OfType<UserMessage>().ToArray();
        Assert.DoesNotContain(userMessages, m => m.Content.OfType<TextContent>().Any(c => c.Text.Contains(promptText)));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ProcessInputAsyncFailureShowsErrorAndRestoresPromptText()
    {
        Func<string, IReadOnlyList<ImageContent>?, string, CancellationToken, Task<TuiInputHookResult>> processInputAsync = (_, _, _, _) =>
            throw new InvalidOperationException("input hook failed before dispatch");

        await using var running = await TuiIntegrationTestHost.StartAsync(processInputAsync: processInputAsync);

        await running.SubmitPromptAsync("hello world");

        var state = running.Context.GetState();
        Assert.Contains(state.Transcript,
            item => item.Role == "system" && item.Text.Contains("input hook failed before dispatch", StringComparison.Ordinal));
        Assert.Equal("hello world", running.Context.Prompt.PromptText);

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ConfigureUiBridgeIsCalledDuringStartupAndExtensionLoadStatusAppearsAsSystemRow()
    {
        var configureUiBridgeCallCount = 0;
        var statusCallCount = 0;

        void ConfigureUiBridge(ExtensionUiBridgeHost bridge) =>
            Interlocked.Increment(ref configureUiBridgeCallCount);

        TuiExtensionLoadStatus GetExtensionLoadStatus()
        {
            var call = Interlocked.Increment(ref statusCallCount);
            if (call == 1)
                return new TuiExtensionLoadStatus(Total: 5, Active: 5, BlockingActive: 5, Ready: 0, Failed: 0);
            return new TuiExtensionLoadStatus(Total: 5, Active: 0, BlockingActive: 0, Ready: 5, Failed: 0);
        }

        await using var running = await TuiIntegrationTestHost.StartAsync(
            configureUiBridge: ConfigureUiBridge,
            getExtensionLoadStatus: GetExtensionLoadStatus);

        Assert.Equal(1, Volatile.Read(ref configureUiBridgeCallCount));

        await running.WaitForAsync(() =>
        {
            var state = running.Context.GetState();
            return state.Transcript.Any(item =>
                item.Role == "system" &&
                item.Text.Contains("Extensions loaded:", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(5));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExtensionLoadingBlocksRegularMessagesAndNonWhitelistedCommands()
    {
        var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/quit" };
        var statusCallCount = 0;

        TuiExtensionLoadStatus GetExtensionLoadStatus()
        {
            Interlocked.Increment(ref statusCallCount);
            return new TuiExtensionLoadStatus(Total: 3, Active: 3, BlockingActive: 3, Ready: 0, Failed: 0);
        }

        await using var running = await TuiIntegrationTestHost.StartAsync(
            getExtensionLoadStatus: GetExtensionLoadStatus,
            commandWhitelist: whitelist);

        await running.SubmitPromptAsync("hello");

        var state = running.Context.GetState();
        Assert.Contains(state.Transcript,
            item => item.Role == "system" && item.Text.Contains("Extensions are still loading", StringComparison.Ordinal));

        await running.SubmitPromptAsync("/help");

        state = running.Context.GetState();
        Assert.Contains(state.Transcript,
            item => item.Role == "system" && item.Text.Contains("Extensions are still loading", StringComparison.Ordinal));

        await running.StopAsync();
    }

    [Fact]
    public async Task BackgroundExtensionLoadingDoesNotBlockRegularMessages()
    {
        var promptText = "hello while background extensions load";
        var assistantText = "background loading reply";
        var harness = TuiIntegrationTestHost.CreateHarness(assistantText);

        TuiExtensionLoadStatus GetExtensionLoadStatus()
            => new(Total: 3, Active: 3, BlockingActive: 0, Ready: 0, Failed: 0);

        await using var running = await TuiIntegrationTestHost.StartAsync(
            runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harness),
            getExtensionLoadStatus: GetExtensionLoadStatus,
            commandWhitelist: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/quit" });

        await running.SubmitPromptAsync(promptText);

        await running.WaitUntilAsync(text => text.Contains(assistantText));
        var state = running.Context.GetState();
        Assert.DoesNotContain(state.Transcript,
            item => item.Role == "system" && item.Text.Contains("Extensions are still loading", StringComparison.Ordinal));

        await running.StopAsync();
    }

    [Fact]
    public async Task ExtensionLoadingAllowsWhitelistedQuitCommand()
    {
        var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/quit" };
        var dispatchCalled = 0;

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = (request, ct) =>
        {
            Interlocked.Increment(ref dispatchCalled);
            return Task.FromResult(new TuiCommandDispatchResult(true, ShouldExit: true));
        };

        TuiExtensionLoadStatus GetExtensionLoadStatus()
            => new(Total: 3, Active: 3, BlockingActive: 3, Ready: 0, Failed: 0);

        await using var running = await TuiIntegrationTestHost.StartAsync(
            getExtensionLoadStatus: GetExtensionLoadStatus,
            commandWhitelist: whitelist,
            dispatchCommandAsync: dispatchCommandAsync);

        await running.WaitUntilAsync(text => text.Length > 0);

        await running.SubmitPromptAsync("/quit");

        var completed = await Task.WhenAny(running.RunTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(running.RunTask, completed);
        var exitCode = await running.RunTask;
        Assert.Equal(0, exitCode);
        Assert.Equal(1, Volatile.Read(ref dispatchCalled));
    }

    [Fact]
    public async Task QuitCommandBypassesInputHooks()
    {
        var inputHookCalls = 0;
        var dispatchCalled = 0;

        Task<TuiInputHookResult> ProcessInputAsync(string text, IReadOnlyList<ImageContent>? images, string source, CancellationToken token)
        {
            Interlocked.Increment(ref inputHookCalls);
            return Task.FromResult(new TuiInputHookResult(true, text, images));
        }

        Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>> dispatchCommandAsync = (request, ct) =>
        {
            Interlocked.Increment(ref dispatchCalled);
            return Task.FromResult(new TuiCommandDispatchResult(true, ShouldExit: true));
        };

        await using var running = await TuiIntegrationTestHost.StartAsync(
            processInputAsync: ProcessInputAsync,
            dispatchCommandAsync: dispatchCommandAsync);
        await running.WaitUntilAsync(text => text.Length > 0);

        await running.SubmitPromptAsync("/quit");

        var completed = await Task.WhenAny(running.RunTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(running.RunTask, completed);
        var exitCode = await running.RunTask;
        Assert.Equal(0, exitCode);
        Assert.Equal(0, Volatile.Read(ref inputHookCalls));
        Assert.Equal(1, Volatile.Read(ref dispatchCalled));
    }

    [Fact]
    public async Task CustomUiReceivesApplicationKeysBeforePromptAndGlobalShortcuts()
    {
        var forwardedInputs = new List<string>();
        ExtensionUiBridgeHost? capturedBridge = null;

        void ConfigureUiBridge(ExtensionUiBridgeHost bridge)
        {
            capturedBridge = bridge;
            bridge.DispatchUi = action => action();
            bridge.SendCustomUiInputAsync = (requestId, data, width, height, _, _) =>
            {
                if (data is not null)
                    forwardedInputs.Add(data);
                return Task.FromResult(new ExtensionCustomUiSnapshot(
                    requestId, ["Pick", "> Alpha"], width ?? 80, height ?? 24));
            };
        }

        await using var running = await TuiIntegrationTestHost.StartAsync(
            configureUiBridge: ConfigureUiBridge,
            onBeforeRun: (context, ct) =>
            {
                context.Prompt.FocusAtEnd();
                using var document = JsonDocument.Parse("""
                {"requestId":"custom-1","lines":["Pick","> Alpha"],"width":80,"height":24}
                """);
                _ = capturedBridge!.ShowCustomComponentAsync("ext", document.RootElement.Clone(), ct);
                return Task.CompletedTask;
            });

        await running.WaitForAsync(() => capturedBridge!.HasActiveCustomUi, TimeSpan.FromSeconds(5));

        await running.SendApplicationKeyAsync(Key.CursorUp);
        await running.SendApplicationKeyAsync(Key.CursorDown);
        await running.SendApplicationKeyAsync(Key.Esc);
        await running.SendApplicationKeyAsync(Key.C.WithCtrl);
        await running.SendApplicationKeyAsync(Key.D.WithCtrl);
        await running.SendApplicationKeyAsync(Key.L.WithCtrl);
        await running.SendApplicationKeyAsync(Key.Tab.WithShift);

        Assert.Contains("\u001b[A", forwardedInputs);
        Assert.Contains("\u001b[B", forwardedInputs);
        Assert.Contains("\u001b", forwardedInputs);
        Assert.Contains("\u0003", forwardedInputs);
        Assert.Contains("\u0004", forwardedInputs);
        Assert.Contains("\u000c", forwardedInputs);
        Assert.Contains("\u001b[Z", forwardedInputs);
    }

    [Fact]
    public async Task ExtensionNotifyRequestDoesNotBlockModelSelector()
    {
        ExtensionUiBridgeHost? capturedBridge = null;
        var commandCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var running = await TuiIntegrationTestHost.StartAsync(
            configureUiBridge: bridge => capturedBridge = bridge,
            dispatchCommandAsync: async (request, ct) =>
            {
                var selected = await request.SelectAsync("Select model", ["test/test — Test"], ct);
                if (!string.IsNullOrWhiteSpace(selected))
                    commandCompleted.TrySetResult();
                return new TuiCommandDispatchResult(!string.IsNullOrWhiteSpace(selected));
            },
            onBeforeRun: (_, _) => Task.CompletedTask);
        await running.WaitUntilAsync(text => text.Length > 0);

        var notifyTask = capturedBridge!.NotifyAsync("background notice");
        await Task.Delay(100);

        await running.SubmitPromptAsync("/model");

        await running.WaitUntilAsync(text => text.Contains("Select model", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        await running.SendApplicationKeyAsync(Key.Enter);

        var completed = await Task.WhenAny(commandCompleted.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Equal(commandCompleted.Task, completed);
        await notifyTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SubmitPromptAsyncDoesNotBlockUiThreadDuringAgentTurn()
    {
        var agentTurnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentTurnCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = TuiIntegrationTestHost.CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            SlowStream(agentTurnStarted, agentTurnCanComplete),
            TuiIntegrationTestHost.FakeCompletion,
            []));

        await using var running = await TuiIntegrationTestHost.StartAsync(runtime: TuiIntegrationTestHost.CreateRuntimeFacade(harness));
        await running.WaitUntilAsync(text => text.Length > 0);

        await running.SubmitPromptAsync("test prompt");

        await agentTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var uiResponsive = await running.TryUiThreadActionAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(uiResponsive, "UI thread was blocked during agent turn");

        agentTurnCanComplete.TrySetResult();

        await running.WaitUntilAsync(text => text.Contains("slow response", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        var result = await running.StopAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task F6Key_TogglesLeftSidebarVisibilityInState()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().LeftSidebarVisible);

        await running.SendApplicationKeyAsync(Key.F6);

        await running.WaitForAsync(
            () => !running.Context.GetState().LeftSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    [Fact]
    public async Task F7Key_TogglesRightSidebarVisibilityInState()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().RightSidebarVisible);

        await running.SendApplicationKeyAsync(Key.F7);

        await running.WaitForAsync(
            () => !running.Context.GetState().RightSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    [Fact]
    public async Task F6Key_PressedTwice_TogglesLeftSidebarBothWays()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().LeftSidebarVisible);

        await running.SendApplicationKeyAsync(Key.F6);
        await running.WaitForAsync(
            () => !running.Context.GetState().LeftSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.SendApplicationKeyAsync(Key.F6);
        await running.WaitForAsync(
            () => running.Context.GetState().LeftSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    [Fact]
    public async Task F7Key_PressedTwice_TogglesRightSidebarBothWays()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().RightSidebarVisible);

        await running.SendApplicationKeyAsync(Key.F7);
        await running.WaitForAsync(
            () => !running.Context.GetState().RightSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.SendApplicationKeyAsync(Key.F7);
        await running.WaitForAsync(
            () => running.Context.GetState().RightSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    [Fact]
    public async Task F6Key_WhenKeyArrivesHandledDueToDriverInstanceReuse_StillTogglesLeftSidebar()
    {
        // The Windows terminal driver reuses Key instances across key-repeat events.
        // After the first F6 dispatch marks key.Handled = true, every subsequent
        // driver event for F6 arrives at TuiInputRouter with Handled already true.
        // TuiInputRouter must still dispatch these so the sidebar toggles on each press.
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().LeftSidebarVisible);

        var reusedF6 = new Key(KeyCode.F6) { Handled = true };
        await running.SendApplicationKeyAsync(reusedF6);

        await running.WaitForAsync(
            () => !running.Context.GetState().LeftSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    [Fact]
    public async Task F7Key_WhenKeyArrivesHandledDueToDriverInstanceReuse_StillTogglesRightSidebar()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().RightSidebarVisible);

        var reusedF7 = new Key(KeyCode.F7) { Handled = true };
        await running.SendApplicationKeyAsync(reusedF7);

        await running.WaitForAsync(
            () => !running.Context.GetState().RightSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    [Fact]
    public async Task F6Key_WhenKeyIsAlreadyHandledByApplicationRouter_DoesNotReDispatchFromPrompt()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().LeftSidebarVisible);

        // Path A (TuiInputRouter via Application.KeyDown) hides sidebar and marks key as Handled
        await running.SendApplicationKeyAsync(Key.F6);
        await running.WaitForAsync(
            () => !running.Context.GetState().LeftSidebarVisible,
            TimeSpan.FromSeconds(3));

        // Path B (TuiInputCoordinator via prompt.KeyDown) receives the same already-handled key,
        // as happens in the real app when the prompt has focus
        var handledF6 = new Key(KeyCode.F6) { Handled = true };
        await running.SendPromptKeyAsync(handledF6);
        await Task.Delay(300); // allow any re-dispatch to take effect

        // Sidebar must remain hidden; Path B must not re-dispatch the already-handled key
        Assert.False(running.Context.GetState().LeftSidebarVisible);

        await running.StopAsync();
    }

    [Fact]
    public async Task F7Key_WhenKeyIsAlreadyHandledByApplicationRouter_DoesNotReDispatchFromPrompt()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().RightSidebarVisible);

        // Path A hides right sidebar and marks key as Handled
        await running.SendApplicationKeyAsync(Key.F7);
        await running.WaitForAsync(
            () => !running.Context.GetState().RightSidebarVisible,
            TimeSpan.FromSeconds(3));

        // Path B receives the same already-handled key
        var handledF7 = new Key(KeyCode.F7) { Handled = true };
        await running.SendPromptKeyAsync(handledF7);
        await Task.Delay(300);

        // Sidebar must remain hidden; Path B must not re-dispatch the already-handled key
        Assert.False(running.Context.GetState().RightSidebarVisible);

        await running.StopAsync();
    }

    [Fact]
    public async Task TuiHost_RemovesBuiltInF6KeyBinding_AfterInit()
    {
        // F6 is Terminal.Gui's built-in NextTabGroup key. In the real terminal,
        // the NextTabGroup command marks F6 as Handled and shifts focus away from
        // the prompt, silently blocking the sidebar toggle on subsequent presses.
        await using var running = await TuiIntegrationTestHost.StartAsync();

        var tcs = new TaskCompletionSource<(bool f6, bool shiftF6)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Invoke(() =>
        {
            var f6 = Application.KeyBindings.TryGet(Key.F6, out _);
            var shiftF6 = Application.KeyBindings.TryGet(Key.F6.WithShift, out _);
            tcs.SetResult((f6, shiftF6));
        });
        var (f6HasBinding, shiftF6HasBinding) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(f6HasBinding, "F6 must not be in Application.KeyBindings — the built-in NextTabGroup command would block sidebar toggle on subsequent presses");
        Assert.False(shiftF6HasBinding, "Shift+F6 must not be in Application.KeyBindings — the built-in PreviousTabGroup command would block sidebar toggle");

        await running.StopAsync();
    }

    [Fact]
    public async Task InvokeCommand_ToggleLeftSidebar_TogglesLeftSidebarVisibilityInState()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().LeftSidebarVisible);

        Application.Invoke(() => running.Context.InvokeCommand("toggle-left-sidebar"));

        await running.WaitForAsync(
            () => !running.Context.GetState().LeftSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    [Fact]
    public async Task InvokeCommand_ToggleRightSidebar_TogglesRightSidebarVisibilityInState()
    {
        await using var running = await TuiIntegrationTestHost.StartAsync();
        Assert.True(running.Context.GetState().RightSidebarVisible);

        Application.Invoke(() => running.Context.InvokeCommand("toggle-right-sidebar"));

        await running.WaitForAsync(
            () => !running.Context.GetState().RightSidebarVisible,
            TimeSpan.FromSeconds(3));

        await running.StopAsync();
    }

    private static AgentStreamAsync SlowStream(TaskCompletionSource started, TaskCompletionSource canComplete)
    {
        async IAsyncEnumerable<AssistantMessageEvent> Stream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____)
        {
            started.TrySetResult();
            await canComplete.Task;
            var message = new AssistantMessage([new TextContent("slow response")], StopReason: "stop");
            yield return new AssistantMessageEvent.Start(message);
            await Task.Yield();
            yield return new AssistantMessageEvent.Done(message);
        }

        return Stream;
    }
}

file sealed class NonPumpingSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
    }
}

file sealed class DropSecondPostApplicationContext : ITuiApplicationContext
{
    private readonly ITuiApplicationContext _inner = new TerminalGuiApplicationContext();
    private int _postCount;

    public void Post(Action action)
    {
        // Drop exactly the second posted callback; the first (connecting) post and all
        // later posts are forwarded unchanged.
        if (Interlocked.Increment(ref _postCount) == 2)
            return;
        _inner.Post(action);
    }

    public object AddTimeout(TimeSpan interval, Func<bool> callback) => _inner.AddTimeout(interval, callback);
    public void RemoveTimeout(object token) => _inner.RemoveTimeout(token);
    public void RequestStop(Toplevel view) => _inner.RequestStop(view);
    public void Run(Toplevel view) => _inner.Run(view);

    public event EventHandler<Key>? KeyDown
    {
        add => _inner.KeyDown += value;
        remove => _inner.KeyDown -= value;
    }

    public event EventHandler<SizeChangedEventArgs>? SizeChanging
    {
        add => _inner.SizeChanging += value;
        remove => _inner.SizeChanging -= value;
    }
}
