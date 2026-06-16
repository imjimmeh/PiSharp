using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class ExtensionEventBusTests
{
    [Fact]
    public async Task HandlersRunInRegistrationOrder()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        var order = new List<int>();
        bus.On("test:event", (_, _) => { order.Add(1); return Task.CompletedTask; });
        bus.On("test:event", (_, _) => { order.Add(2); return Task.CompletedTask; });

        await bus.EmitAsync("test:event", new object());

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task DisposerRemovesHandler()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        var callCount = 0;
        var disposable = bus.On("test:event", (_, _) => { callCount++; return Task.CompletedTask; });
        disposable.Dispose();

        await bus.EmitAsync("test:event", new object());

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task ThrowingHandlerIsIsolatedAndLaterHandlersStillRun()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        var laterRan = false;
        bus.On("test:event", (_, _) => throw new InvalidOperationException("handler failed"));
        bus.On("test:event", (_, _) => { laterRan = true; return Task.CompletedTask; });

        await bus.EmitAsync("test:event", new object());

        Assert.True(laterRan);
    }

    [Fact]
    public async Task ThrowingHandlerExceptionIsCapturedAsDiagnostic()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        bus.On("test:event", (_, _) => throw new InvalidOperationException("handler failed"));

        await bus.EmitAsync("test:event", new object());

        var diagnostic = Assert.Single(bus.Diagnostics);
        Assert.IsType<InvalidOperationException>(diagnostic);
        Assert.Equal("handler failed", diagnostic.Message);
    }

    [Fact]
    public async Task BusClearsOnReset()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        var callCount = 0;
        bus.On("test:event", (_, _) => { callCount++; return Task.CompletedTask; });

        bus.Clear();
        await bus.EmitAsync("test:event", new object());

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task OperationCanceledExceptionPropagatesWhenTokenCanceled()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        bus.On("test:event", (_, ct) => Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            bus.EmitAsync("test:event", new object(), cts.Token));
    }

    [Fact]
    public async Task EmitAsyncInvokesForwardDelegateAfterNativeHandlers()
    {
        var registry = new ExtensionRegistry();
        var forwardOrder = new List<string>();
        Func<string, object?, CancellationToken, Task>? emitBridge = async (channel, payload, ct) =>
        {
            forwardOrder.Add("forward");
        };
        var bus = new ExtensionEventBus(registry, "test-bus", emitBridge);

        var nativeRan = false;
        bus.On("test:event", (_, _) => { nativeRan = true; forwardOrder.Add("native"); return Task.CompletedTask; });

        await bus.EmitAsync("test:event", new object());

        Assert.True(nativeRan);
        Assert.Equal(["native", "forward"], forwardOrder);
    }

    [Fact]
    public async Task ForwardDelegateExceptionDoesNotStopEmit()
    {
        var registry = new ExtensionRegistry();
        Func<string, object?, CancellationToken, Task>? emitBridge = (_, _, _) =>
            throw new InvalidOperationException("forward failed");

        var bus = new ExtensionEventBus(registry, "test-bus", emitBridge);

        bus.On("test:event", (_, _) => Task.CompletedTask);
        await bus.EmitAsync("test:event", new object());

        var diagnostic = Assert.Single(bus.Diagnostics);
        Assert.Equal("forward failed", diagnostic.Message);
    }

    [Fact]
    public async Task ForwardDelegateCancellationPropagatesWhenTokenCanceled()
    {
        var registry = new ExtensionRegistry();
        Func<string, object?, CancellationToken, Task>? emitBridge = (_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var bus = new ExtensionEventBus(registry, "test-bus", emitBridge);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            bus.EmitAsync("test:event", new object(), cts.Token));
    }

    [Fact]
    public async Task ExtensionEventBusDisposeClearsHandlers()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        var callCount = 0;
        bus.On("test:event", (_, _) => { callCount++; return Task.CompletedTask; });

        bus.Dispose();
        await bus.EmitAsync("test:event", new object());

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task MultipleThrowingHandlersAllCapturedAsDiagnostics()
    {
        var registry = new ExtensionRegistry();
        var bus = new ExtensionEventBus(registry, "test-bus");

        bus.On("test:event", (_, _) => throw new InvalidOperationException("first failed"));
        bus.On("test:event", (_, _) => throw new ArgumentException("second failed"));

        await bus.EmitAsync("test:event", new object());

        Assert.Equal(2, bus.Diagnostics.Count);
    }
}
