using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class RenderStateStoreTests
{
    private static TuiRenderState Fresh()
        => TuiRenderState.Empty("s1", null, new ModelDescriptor("p", "m", "name"), ThinkingLevel.Off, null);

    [Fact]
    public void Snapshot_returns_initial_state()
    {
        var store = new RenderStateStore(Fresh());
        Assert.Equal(ThinkingLevel.Off, store.Snapshot().ThinkingLevel);
    }

    [Fact]
    public void Replace_updates_snapshot()
    {
        var store = new RenderStateStore(Fresh());
        store.Replace(Fresh() with { IsBusy = true });
        Assert.True(store.Snapshot().IsBusy);
    }

    [Fact]
    public void Update_is_atomic_and_returns_new_state()
    {
        var store = new RenderStateStore(Fresh());
        var next = store.Update(s => s with { PendingMessageCount = s.PendingMessageCount + 1 });
        Assert.Equal(1, next.PendingMessageCount);
        Assert.Equal(1, store.Snapshot().PendingMessageCount);
    }

    [Fact]
    public void Concurrent_updates_do_not_lose_transitions()
    {
        var store = new RenderStateStore(Fresh());
        Parallel.For(0, 500, _ => store.Update(s => s with { PendingMessageCount = s.PendingMessageCount + 1 }));
        Assert.Equal(500, store.Snapshot().PendingMessageCount);
    }
}
