using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.TsBridge;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class ChildSessionEventBatchWorkerTests
{
    [Fact]
    public async Task BatchWorker_task_completes_when_NotifyAsync_throws_IOException()
    {
        var channel = Channel.CreateUnbounded<TsExtensionHost.ChildSessionEventForward>();
        var fakeClient = new ThrowingNotifyClient(new IOException("pipe closed"));
        var cts = new CancellationTokenSource();

        var workerTask = TsExtensionHost.RunBatchWorkerAsync(
            fakeClient, channel.Reader, NullLogger.Instance, cts.Token);

        await channel.Writer.WriteAsync(new TsExtensionHost.ChildSessionEventForward("session-1", new { }), cts.Token);

        // Allow the worker to process the event
        await Task.Delay(200);

        Assert.True(workerTask.IsCompleted, "Worker should stop when pipe throws IOException");
        Assert.False(workerTask.IsFaulted, "Worker should exit cleanly, not crash");
    }

    [Fact]
    public async Task BatchWorker_continues_on_non_IOException()
    {
        var channel = Channel.CreateUnbounded<TsExtensionHost.ChildSessionEventForward>();
        var fakeClient = new CountingNotifyClient();
        var cts = new CancellationTokenSource();

        var workerTask = TsExtensionHost.RunBatchWorkerAsync(
            fakeClient, channel.Reader, NullLogger.Instance, cts.Token);

        await channel.Writer.WriteAsync(new TsExtensionHost.ChildSessionEventForward("session-1", new { }), cts.Token);
        await Task.Delay(100);

        // Worker should still be running (processing normally)
        Assert.False(workerTask.IsCompleted, "Worker should keep running when notifications succeed");

        // Clean up
        await cts.CancelAsync();
        channel.Writer.Complete();
        await workerTask;
    }

    private sealed class ThrowingNotifyClient(IOException exception) : ITsBridgeClient
    {
        public bool IsStarted => true;
        public IReadOnlyList<string> RecentStandardError => [];

        public Task StartAsync(Func<JsonRpcRequest, CancellationToken, Task<object?>> requestHandler, object initializePayload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
            => Task.FromResult(default(JsonElement));

        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
            => Task.FromException(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingNotifyClient : ITsBridgeClient
    {
        public int CallCount { get; private set; }
        public bool IsStarted => true;
        public IReadOnlyList<string> RecentStandardError => [];

        public Task StartAsync(Func<JsonRpcRequest, CancellationToken, Task<object?>> requestHandler, object initializePayload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
            => Task.FromResult(default(JsonElement));

        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
