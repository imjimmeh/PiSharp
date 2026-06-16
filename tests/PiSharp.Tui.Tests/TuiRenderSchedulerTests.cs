using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Shell;
using PiSharp.Agent.Core.Models;
using PiSharp.Abstractions.Options;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiRenderSchedulerTests
{
    [Fact]
    public void TimingOptionsDefaultsUseEightMillisecondCadence()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(8), TuiTimingOptions.Default.RenderFrameInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(8), TuiTimingOptions.Default.HarnessEventBatchInterval);
    }

    [Fact]
    public void RenderSchedulerCoalescesBurstRequestsIntoOneFrame()
    {
        var frames = new List<Action>();
        var renders = 0;
        var scheduler = new TuiRenderScheduler(dispatch: action => action(), scheduleFrame: (action, _) => frames.Add(action));

        for (var index = 0; index < 100; index++) scheduler.RequestRender(() => renders++);

        var frame = Assert.Single(frames);
        Assert.Equal(0, renders);

        frame();

        Assert.Equal(1, renders);
    }

    [Fact]
    public void RenderSchedulerAllowsNextFrameAfterPriorRenderFlushes()
    {
        var frames = new List<Action>();
        var renders = 0;
        var scheduler = new TuiRenderScheduler(dispatch: action => action(), scheduleFrame: (action, _) => frames.Add(action));

        scheduler.RequestRender(() => renders++);
        frames.Single()();

        scheduler.RequestRender(() => renders++);
        Assert.Equal(2, frames.Count);
        frames[1]();

        Assert.Equal(2, renders);
    }

    [Fact]
    public void RenderSchedulerFlushesSynchronousRequestsWithoutAnimationFrameDelay()
    {
        var renders = 0;
        var scheduler = new TuiRenderScheduler(dispatch: action => action());

        scheduler.RequestRender(() => renders++);

        Assert.Equal(1, renders);
    }

    [Fact]
    public void RenderSchedulerDispatchesFrameThroughProvidedDispatcher()
    {
        var frames = new List<Action>();
        var dispatched = 0;
        var renders = 0;
        var scheduler = new TuiRenderScheduler(dispatch: action =>
        {
            dispatched++;
            action();
        }, scheduleFrame: (action, _) => frames.Add(action));

        scheduler.RequestRender(() => renders++);
        frames.Single()();

        Assert.Equal(1, dispatched);
        Assert.Equal(1, renders);
    }

    [Fact]
    public void RenderSchedulerCancelledFrameDoesNotBlockFutureRenders()
    {
        var frames = new List<Action>();
        var renders = 0;
        using var cancellation = new CancellationTokenSource();
        var scheduler = new TuiRenderScheduler(dispatch: action => action(), scheduleFrame: (action, _) => frames.Add(action));

        scheduler.RequestRender(() => renders++, cancellation.Token);
        cancellation.Cancel();
        frames.Single()();

        scheduler.RequestRender(() => renders++);
        Assert.Equal(2, frames.Count);
        frames[1]();

        Assert.Equal(1, renders);
    }

    [Fact]
    public void PromptAndSuggestionRenderPathsUseScheduler()
    {
        var frames = new List<Action>();
        var renders = 0;
        var scheduler = new TuiRenderScheduler(dispatch: action => action(), scheduleFrame: (action, _) => frames.Add(action));
        var prompt = new PromptEditor
        {
            CompleteText = text => text.StartsWith("/", StringComparison.Ordinal) ? ["/help", "/model"] : []
        };
        using var subscription = TuiRenderRequestRouter.ConnectPromptSuggestions(prompt, () => scheduler.RequestRender(() => renders++));

        prompt.SetPromptText("/");
        prompt.MoveSuggestionSelection(1);
        prompt.MoveSuggestionSelection(-1);

        var frame = Assert.Single(frames);
        Assert.Equal(0, renders);

        frame();

        Assert.Equal(1, renders);
    }

    [Fact]
    public void RenderCoordinatorCoalescesBurstRenderRequestsBeforeUiDispatch()
    {
        var appContext = new FakeTuiApplicationContext();
        var shell = new TuiShellView();
        var state = Empty();
        using var coordinator = new TuiRenderCoordinator(
            shell,
            () => state,
            next => state = next,
            appContext,
            () => new TuiFooterSnapshot("cwd", null, 0, 0, 0, 0, 0, 0, 0, false, new Dictionary<string, string>()),
            renderFrameInterval: TimeSpan.Zero);

        for (var index = 0; index < 100; index++)
        {
            coordinator.RequestRender();
        }

        Assert.True(
            appContext.Dispatcher.Posted.Count <= 1,
            $"Expected burst render requests to avoid queuing one UI action per update, but queued {appContext.Dispatcher.Posted.Count} actions.");
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test", ContextWindow: 100), ThinkingLevel.Off, null);
}
