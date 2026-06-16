using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiCommandControllerTests
{
    [Fact]
    public async Task HelpCommandAppendsBuiltInHelpAndHotkeys()
    {
        var state = Empty();
        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => { },
            () => { },
            () => "HOTKEYS"));

        var handled = await controller.TryHandleCommandAsync("/help", CancellationToken.None);

        Assert.True(handled);
        var item = Assert.Single(state.Transcript);
        Assert.Equal("system", item.Role);
        Assert.StartsWith("Commands:", item.Text, StringComparison.Ordinal);
        Assert.Contains("/abort", item.Text);
        Assert.Contains("HOTKEYS", item.Text);
    }

    [Fact]
    public async Task BuiltInCommandsClearAbortAndExitThroughInjectedCapabilities()
    {
        var aborts = 0;
        var exits = 0;
        var state = Empty().AppendSystem("existing");
        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => aborts++,
            () => exits++,
            () => "HOTKEYS"));

        Assert.True(await controller.TryHandleCommandAsync("/clear", CancellationToken.None));
        Assert.Empty(state.Transcript);

        Assert.True(await controller.TryHandleCommandAsync("/abort", CancellationToken.None));
        Assert.Equal(1, aborts);
        Assert.Contains(state.Transcript, item => item.Text == "Abort requested.");

        Assert.True(await controller.TryHandleCommandAsync("/exit", CancellationToken.None));
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task UnknownCommandWithoutDispatcherAppendsErrorAndIsHandled()
    {
        var state = Empty();
        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => { },
            () => { },
            () => "HOTKEYS"));

        var handled = await controller.TryHandleCommandAsync("/missing", CancellationToken.None);

        Assert.True(handled);
        var item = Assert.Single(state.Transcript);
        Assert.True(item.IsError);
        Assert.Contains("Unknown command: /missing", item.Text);
    }

    [Fact]
    public async Task DispatchCommandRunsAsynchronouslyAllowsConcurrentDispatchAndRefreshesSessionOnCompletion()
    {
        var state = Empty();
        var refreshes = 0;
        string? firstDispatched = null;
        string? secondDispatched = null;
        var firstCompletion = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => { },
            () => { },
            () => "HOTKEYS",
            command => new TuiCommandDispatchRequest(command, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _, _) => Task.CompletedTask),
            (request, _) =>
            {
                if (request.Text == "/model") { firstDispatched = request.Text; return firstCompletion.Task; }
                if (request.Text == "/session") { secondDispatched = request.Text; return secondCompletion.Task; }
                return Task.FromResult(new TuiCommandDispatchResult(true));
            },
            _ => { refreshes++; return Task.CompletedTask; },
            () => AgentHarnessPhase.Idle));

        Assert.True(await controller.TryHandleCommandAsync("/model", CancellationToken.None));
        Assert.True(controller.IsCommandInProgress);
        Assert.Equal("/model", firstDispatched);

        Assert.True(await controller.TryHandleCommandAsync("/session", CancellationToken.None));
        Assert.Equal("/session", secondDispatched);

        firstCompletion.SetResult(new TuiCommandDispatchResult(true));
        await WaitUntilAsync(() => refreshes > 0);

        secondCompletion.SetResult(new TuiCommandDispatchResult(true));
        await WaitUntilAsync(() => !controller.IsCommandInProgress);

        Assert.True(refreshes >= 1);
        Assert.Equal("Idle", state.Status);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task ResumeCommandsShowAndClearLoadingStateAroundDispatch()
    {
        var state = Empty();
        var dispatchCompletion = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => { },
            () => { },
            () => "HOTKEYS",
            command => new TuiCommandDispatchRequest(command, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _, _) => Task.CompletedTask),
            (_, _) => dispatchCompletion.Task,
            GetCurrentPhase: () => AgentHarnessPhase.Idle));

        Assert.True(await controller.TryHandleCommandAsync("/session", CancellationToken.None));
        Assert.True(state.IsBusy);
        Assert.Equal("Loading sessions", state.Status);
        Assert.Equal("Loading sessions...", state.WorkingMessage);

        dispatchCompletion.SetResult(new TuiCommandDispatchResult(true));
        await WaitUntilAsync(() => !controller.IsCommandInProgress);

        Assert.False(state.IsBusy);
        Assert.Equal("Idle", state.Status);
        Assert.Null(state.WorkingMessage);
    }

    [Fact]
    public async Task NonCommandTextIsNotHandled()
    {
        var state = Empty();
        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => { },
            () => { },
            () => "HOTKEYS"));

        Assert.False(await controller.TryHandleCommandAsync("hello", CancellationToken.None));
        Assert.Empty(state.Transcript);
    }

    [Fact]
    public async Task BuiltInCommandsWorkWhileDispatchIsInProgress()
    {
        var state = Empty();
        var aborts = 0;
        var exits = 0;
        var dispatchCompletion = new TaskCompletionSource<TuiCommandDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => aborts++,
            () => exits++,
            () => "HOTKEYS",
            command => new TuiCommandDispatchRequest(command, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _, _) => Task.CompletedTask),
            (_, _) => dispatchCompletion.Task));

        Assert.True(await controller.TryHandleCommandAsync("/model", CancellationToken.None));
        Assert.True(controller.IsCommandInProgress);

        Assert.True(await controller.TryHandleCommandAsync("/help", CancellationToken.None));
        Assert.Contains(state.Transcript, item => item.Text.Contains("Commands:", StringComparison.Ordinal));

        Assert.True(await controller.TryHandleCommandAsync("/abort", CancellationToken.None));
        Assert.Equal(1, aborts);
        Assert.Contains(state.Transcript, item => item.Text.Contains("Abort requested.", StringComparison.Ordinal));

        Assert.True(await controller.TryHandleCommandAsync("/clear", CancellationToken.None));
        Assert.Empty(state.Transcript);

        Assert.True(await controller.TryHandleCommandAsync("/exit", CancellationToken.None));
        Assert.Equal(1, exits);

        Assert.True(await controller.TryHandleCommandAsync("/hotkeys", CancellationToken.None));
        Assert.Contains(state.Transcript, item => item.Text.Contains("HOTKEYS", StringComparison.Ordinal));

        Assert.True(await controller.TryHandleCommandAsync("/session", CancellationToken.None));

        dispatchCompletion.SetResult(new TuiCommandDispatchResult(true));
        await WaitUntilAsync(() => !controller.IsCommandInProgress);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
}
