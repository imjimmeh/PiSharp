using PiSharp.Tools.Shared;
using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests.Shared;

public sealed class FileMutationQueueTests
{
    [Fact]
    public async Task RunAsyncSerializesSameFileMutationsInFifoOrder()
    {
        var env = new FakeExecutionEnv();
        var order = new List<int>();
        var first = FileMutationQueue.RunAsync(env, "a.txt", async () => { await Task.Delay(20); order.Add(1); return 1; });
        var second = FileMutationQueue.RunAsync(env, "a.txt", async () => { order.Add(2); return 2; });
        await Task.WhenAll(first, second);
        Assert.Equal([1, 2], order);
    }
}
