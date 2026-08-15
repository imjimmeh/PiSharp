using PiSharp.Agent.Core.Events;
using PiSharp.Tui.Interactive.Harness;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiHarnessEventPumpTests
{
    [Fact]
    public async Task Batch_reduction_runs_on_the_pump_worker_without_ui_dispatch()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var reduceThreadId = -1;
        var done = new TaskCompletionSource();

        using var pump = new TuiHarnessEventPump(
            batch => { reduceThreadId = Environment.CurrentManagedThreadId; done.SetResult(); },
            batchInterval: TimeSpan.FromMilliseconds(20),
            capacity: 1024,
            batchSize: 4);

        pump.Enqueue(new AgentHarnessEvent.Core(new AgentEvent.TurnStart()), CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The reducer ran on the pump worker, not on the enqueue caller's thread.
        // Capturing the caller id BEFORE enqueue avoids comparing against the post-await
        // continuation thread (a pool thread that could coincide with the worker and turn
        // the assertion flaky).
        Assert.NotEqual(callerThreadId, reduceThreadId);
    }
}
