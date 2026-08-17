using PiSharp.Abstractions.Tasks;
using Xunit;

namespace PiSharp.Runtime.Tests.Tasks;

public sealed class BackgroundTaskTrackerTests
{
    [Fact]
    public async Task Run_ExecutesActionAndTracksCompletion()
    {
        using var tracker = new BackgroundTaskTracker();
        var ran = false;

        await tracker.Run("test-task", ct =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
        Assert.True(tracker.IsHealthy);
        Assert.Null(tracker.LastFault);
    }

    [Fact]
    public async Task Track_CapturesFaultAndRaisesEvent()
    {
        using var tracker = new BackgroundTaskTracker();
        string? faultedName = null;
        Exception? faultedException = null;

        tracker.TaskFaulted += (name, ex) =>
        {
            faultedName = name;
            faultedException = ex;
        };

        var tcs = new TaskCompletionSource();
        tracker.Track("failing-task", tcs.Task);

        tcs.SetException(new InvalidOperationException("Simulated background error"));

        // Wait briefly for continuation on ThreadPool
        await Task.Delay(50);

        Assert.False(tracker.IsHealthy);
        Assert.NotNull(tracker.LastFault);
        Assert.Equal("Simulated background error", tracker.LastFault!.Message);
        Assert.Equal("failing-task", faultedName);
        Assert.NotNull(faultedException);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightTasksAndDrainsGracefully()
    {
        var tracker = new BackgroundTaskTracker();
        var wasCancelled = false;

        _ = tracker.Run("long-running", async ct =>
        {
            try
            {
                await Task.Delay(10000, ct);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }
        });

        await tracker.DisposeAsync();

        Assert.True(wasCancelled);
        Assert.Empty(tracker.ActiveTasks);
    }
}
