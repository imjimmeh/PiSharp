using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Input;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiInputRouterTests
{
    private sealed class TestInputCapture(string name, Func<Key, bool> handler) : ITuiInputCapture
    {
        public string Name => name;
        public bool TryHandleKey(Key key) => handler(key);
    }

    [Fact]
    public void RouterGivesActiveCaptureFirstRefusal()
    {
        var handledByCapture = false;
        var shortcutInvoked = false;
        var key = Key.Esc;
        var appContext = new FakeTuiApplicationContext();
        var capture = new TestInputCapture("custom-ui", _ => { handledByCapture = true; return true; });
        var router = new TuiInputRouter(
            appContext,
            () => capture,
            _ => false,
            _ => { shortcutInvoked = true; return true; });

        router.HandleKeyForTest(key);

        Assert.True(handledByCapture);
        Assert.False(shortcutInvoked);
        Assert.True(key.Handled);
    }

    [Fact]
    public void ActiveCaptureReceivesHandledNonNavigationKeyAsFreshInput()
    {
        var handledByCapture = false;
        var hostInvoked = false;
        var key = new Key(KeyCode.Enter)
        {
            Handled = true
        };
        var appContext = new FakeTuiApplicationContext();
        var capture = new TestInputCapture("custom-ui", _ => { handledByCapture = true; return true; });
        var router = new TuiInputRouter(
            appContext,
            () => capture,
            _ => { hostInvoked = true; return true; },
            _ => false);

        router.HandleKeyForTest(key);

        Assert.True(handledByCapture);
        Assert.False(hostInvoked);
        Assert.True(key.Handled);
    }

    [Fact]
    public void ActiveCaptureReceivesRepeatedAlreadyHandledKeysAsFreshInput()
    {
        var handledKeys = new List<KeyCode>();
        var hostInvocations = 0;
        var appContext = new FakeTuiApplicationContext();
        var capture = new TestInputCapture("custom-ui", key =>
        {
            handledKeys.Add(key.KeyCode);
            return true;
        });
        var router = new TuiInputRouter(
            appContext,
            () => capture,
            _ => { hostInvocations++; return true; },
            _ => false);

        var down = new Key(KeyCode.CursorDown) { Handled = true };
        var enter = new Key(KeyCode.Enter) { Handled = true };

        router.HandleKeyForTest(down);
        router.HandleKeyForTest(enter);

        Assert.Equal([KeyCode.CursorDown, KeyCode.Enter], handledKeys);
        Assert.Equal(0, hostInvocations);
        Assert.True(down.Handled);
        Assert.True(enter.Handled);
    }

    [Fact]
    public void CaptureDeclineFallsThroughToHostPolicy()
    {
        var hostInvoked = false;
        var key = Key.Esc;
        var appContext = new FakeTuiApplicationContext();
        var capture = new TestInputCapture("declining-capture", _ => false);
        var router = new TuiInputRouter(
            appContext,
            () => capture,
            _ => { hostInvoked = true; return true; },
            _ => false);

        router.HandleKeyForTest(key);

        Assert.True(hostInvoked);
        Assert.True(key.Handled);
    }

    [Fact]
    public void HostPolicyDeclineFallsThroughToShortcutPolicy()
    {
        var shortcutInvoked = false;
        var key = Key.Esc;
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => { shortcutInvoked = true; return true; });

        router.HandleKeyForTest(key);

        Assert.True(shortcutInvoked);
        Assert.True(key.Handled);
    }

    [Fact]
    public void NoPolicyHandlingLeavesKeyUnhandled()
    {
        var key = Key.Esc;
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => false);

        router.HandleKeyForTest(key);

        Assert.False(key.Handled);
    }

    [Fact]
    public void ReusedHandledVerticalArrowIsTreatedAsFreshInput()
    {
        var hostInvocations = 0;
        var key = Key.CursorDown;
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => { hostInvocations++; return true; },
            _ => false);

        router.HandleKeyForTest(key);
        router.HandleKeyForTest(key);

        Assert.Equal(2, hostInvocations);
        Assert.True(key.Handled);
    }

    [Theory]
    [InlineData(KeyCode.Home)]
    [InlineData(KeyCode.PageUp)]
    [InlineData(KeyCode.PageDown)]
    [InlineData(KeyCode.End)]
    public void ReusedHandledTranscriptNavigationKeyIsTreatedAsFreshInput(KeyCode keyCode)
    {
        var hostInvocations = 0;
        var key = new Key(keyCode);
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => { hostInvocations++; return true; },
            _ => false);

        router.HandleKeyForTest(key);
        router.HandleKeyForTest(key);

        Assert.Equal(2, hostInvocations);
        Assert.True(key.Handled);
    }

    [Fact]
    public void ReusedHandledCtrlVerticalArrowIsTreatedAsFreshInput()
    {
        var hostInvocations = 0;
        var key = Key.CursorDown.WithCtrl;
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => { hostInvocations++; return true; },
            _ => false);

        router.HandleKeyForTest(key);
        router.HandleKeyForTest(key);

        Assert.Equal(2, hostInvocations);
        Assert.True(key.Handled);
    }

    [Fact]
    public void HandledShiftTabStillFallsThroughToShortcutPolicy()
    {
        var shortcutInvoked = false;
        var key = Key.Tab.WithShift;
        key.Handled = true;
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ =>
            {
                shortcutInvoked = true;
                return true;
            });

        router.HandleKeyForTest(key);

        Assert.True(shortcutInvoked);
        Assert.True(key.Handled);
    }

    [Fact]
    public void AttachSubscribesExactlyOneAppContextKeyHandler()
    {
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => false);

        Assert.Equal(0, appContext.KeyDownHandlerCount);
        router.Attach();
        Assert.Equal(1, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void DisposeUnsubscribesAppContextKeyHandler()
    {
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => false);

        router.Attach();
        Assert.Equal(1, appContext.KeyDownHandlerCount);

        router.Dispose();
        Assert.Equal(0, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void AttachIsIdempotent()
    {
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => false);

        router.Attach();
        router.Attach();
        Assert.Equal(1, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => false);

        router.Attach();
        router.Dispose();
        router.Dispose();
        Assert.Equal(0, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void AttachAfterDisposeThrowsAndDoesNotSubscribe()
    {
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => false);

        router.Attach();
        router.Dispose();

        var ex = Assert.Throws<ObjectDisposedException>(() => router.Attach());
        Assert.Contains(nameof(TuiInputRouter), ex.ObjectName);

        Assert.Equal(0, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void DisposeBeforeAttachThenAttachThrowsAndDoesNotSubscribe()
    {
        var appContext = new FakeTuiApplicationContext();
        var router = new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            _ => false);

        router.Dispose();

        var ex = Assert.Throws<ObjectDisposedException>(() => router.Attach());
        Assert.Contains(nameof(TuiInputRouter), ex.ObjectName);

        Assert.Equal(0, appContext.KeyDownHandlerCount);
    }

    [Fact]
    public void ConstructorThrowsOnNullAppContext()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new TuiInputRouter(
            null!,
            () => null,
            _ => false,
            _ => false));
        Assert.Equal("appContext", ex.ParamName);
    }

    [Fact]
    public void ConstructorThrowsOnNullGetActiveCapture()
    {
        var appContext = new FakeTuiApplicationContext();
        var ex = Assert.Throws<ArgumentNullException>(() => new TuiInputRouter(
            appContext,
            null!,
            _ => false,
            _ => false));
        Assert.Equal("getActiveCapture", ex.ParamName);
    }

    [Fact]
    public void ConstructorThrowsOnNullTryHandleHostInput()
    {
        var appContext = new FakeTuiApplicationContext();
        var ex = Assert.Throws<ArgumentNullException>(() => new TuiInputRouter(
            appContext,
            () => null,
            null!,
            _ => false));
        Assert.Equal("tryHandleHostInput", ex.ParamName);
    }

    [Fact]
    public void ConstructorThrowsOnNullTryDispatchShortcut()
    {
        var appContext = new FakeTuiApplicationContext();
        var ex = Assert.Throws<ArgumentNullException>(() => new TuiInputRouter(
            appContext,
            () => null,
            _ => false,
            null!));
        Assert.Equal("tryDispatchShortcut", ex.ParamName);
    }
}
