using System.Text.Json;
using System.Drawing;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Input;
using PiSharp.Tui.Interactive.Theme;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class ExtensionCustomUiBridgeTests
{
    [Theory]
    [InlineData("CursorDown", "\u001b[B")]
    [InlineData("CursorUp", "\u001b[A")]
    [InlineData("Enter", "\r")]
    [InlineData("Escape", "\u001b")]
    [InlineData("Space", " ")]
    public void ExtensionCustomUiOverlayTranslatesKeysToTerminalSequences(string keyName, string expected)
    {
        Assert.Equal(expected, ExtensionCustomUiInputTranslator.TranslateKeyNameForTest(keyName));
    }

    [Fact]
    public void ExtensionCustomUiOverlayTranslatesNavigationEditingAndControlKeys()
    {
        AssertTranslated(new Key(KeyCode.CursorLeft), "\u001b[D");
        AssertTranslated(new Key(KeyCode.CursorRight), "\u001b[C");
        AssertTranslated(new Key(KeyCode.Tab), "\t");
        AssertTranslated(new Key(KeyCode.Tab | KeyCode.ShiftMask), "\u001b[Z");
        AssertTranslated(new Key(KeyCode.Backspace), "\u007f");
        AssertTranslated(new Key(KeyCode.Delete), "\u001b[3~");
        AssertTranslated(new Key(KeyCode.Home), "\u001b[H");
        AssertTranslated(new Key(KeyCode.End), "\u001b[F");
        AssertTranslated(new Key(KeyCode.PageUp), "\u001b[5~");
        AssertTranslated(new Key(KeyCode.PageDown), "\u001b[6~");
        AssertTranslated(Key.C.WithCtrl, "\u0003");
        AssertTranslated(Key.D.WithCtrl, "\u0004");
        AssertTranslated(Key.H.WithCtrl, "\u0008");
        AssertTranslated(Key.L.WithCtrl, "\u000c");
        AssertTranslated(Key.O.WithCtrl, "\u000f");
        AssertTranslated(Key.R.WithCtrl, "\u0012");
        AssertTranslated(Key.T.WithCtrl, "\u0014");
        AssertTranslated(Key.U.WithCtrl, "\u0015");
        AssertTranslated(new Key((KeyCode)']').WithCtrl, "\u001d");

        static void AssertTranslated(Key key, string expected)
        {
            Assert.True(ExtensionCustomUiInputTranslator.TryTranslate(key, out var data));
            Assert.Equal(expected, data);
        }
    }

    [Fact]
    public void ExtensionCustomUiOverlayTranslatesPrimaryClickToSgrMouseSequence()
    {
        var data = ExtensionCustomUiInputTranslator.TranslateMouseClickForTest(x: 4, y: 2);

        Assert.Equal("\u001b[<0;5;3M", data);
    }

    [Fact]
    public void ExtensionCustomUiOverlayTranslatesCtrlJToLineFeed()
    {
        var translated = ExtensionCustomUiInputTranslator.TryTranslate(Key.J.WithCtrl, out var data);

        Assert.True(translated);
        Assert.Equal("\n", data);
    }

    [Fact]
    public void ExtensionCustomUiOverlayTranslatesCtrlMToCarriageReturn()
    {
        var translated = ExtensionCustomUiInputTranslator.TryTranslate(Key.M.WithCtrl, out var data);

        Assert.True(translated);
        Assert.Equal("\r", data);
    }

    [Fact]
    public async Task ExtensionCustomUiOverlayForwardsPrimaryClickThroughCustomUiTransport()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var forwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) =>
            {
                forwarded.TrySetResult(data ?? string.Empty);
                return Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data));
            }
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);

        var args = new MouseEventArgs
        {
            Flags = MouseFlags.Button1Clicked,
            Position = new Point(4, 2),
            ScreenPosition = new Point(4, 2),
            View = overlay
        };
        var handled = overlay.NewMouseEvent(args);

        Assert.True(handled);
        Assert.True(args.Handled);

        Assert.Equal("\u001b[<0;5;3M", await forwarded.Task);

        var result = await showTask;

        Assert.True(result.Ok);
        Assert.Equal("\u001b[<0;5;3M", Assert.IsType<string>(result.Value));
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ExtensionCustomUiCaptureRoutesAlreadyHandledEnterThroughCustomUiTransport()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var forwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostInputInvoked = false;
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) =>
            {
                forwarded.TrySetResult(data ?? string.Empty);
                return Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data));
            }
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var capture = new ExtensionCustomUiInputCapture(host);
        var router = new TuiInputRouter(
            new FakeTuiApplicationContext(),
            () => host.HasActiveCustomUi ? capture : null,
            _ => { hostInputInvoked = true; return true; },
            _ => false);
        var enter = new Key(KeyCode.Enter) { Handled = true };

        router.HandleKeyForTest(enter);

        Assert.Equal("\r", await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(enter.Handled);
        Assert.False(hostInputInvoked);

        var result = await showTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Ok);
        Assert.Equal("\r", Assert.IsType<string>(result.Value));
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ExtensionCustomUiInputForwardingDoesNotCaptureUiSynchronizationContext()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var firstResponse = new TaskCompletionSource<ExtensionCustomUiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondForwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) =>
            {
                sendCount++;
                if (sendCount == 1)
                    return firstResponse.Task;

                secondForwarded.TrySetResult(data ?? string.Empty);
                return Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data));
            }
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());

            Assert.True(host.TryHandleCustomUiKey(new Key(KeyCode.CursorDown)));
            Assert.True(host.TryHandleCustomUiKey(new Key(KeyCode.Enter)));

            firstResponse.SetResult(new ExtensionCustomUiSnapshot("custom-1", ["Pick"], 80, 24));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.Equal("\r", await secondForwarded.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var result = await showTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Ok);
        Assert.Equal("\r", Assert.IsType<string>(result.Value));
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ExtensionCustomUiInputForwardingInvokesSenderWithoutUiSynchronizationContext()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var firstResponse = new TaskCompletionSource<ExtensionCustomUiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondForwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = async (requestId, data, width, height, _, _) =>
            {
                sendCount++;
                if (sendCount == 1)
                    return await firstResponse.Task;

                secondForwarded.TrySetResult(data ?? string.Empty);
                return new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data);
            }
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());

            Assert.True(host.TryHandleCustomUiKey(new Key(KeyCode.CursorDown)));
            Assert.True(host.TryHandleCustomUiKey(new Key(KeyCode.Enter)));

            firstResponse.SetResult(new ExtensionCustomUiSnapshot("custom-1", ["Pick"], 80, 24));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.Equal("\r", await secondForwarded.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var result = await showTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Ok);
        Assert.Equal("\r", Assert.IsType<string>(result.Value));
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ExtensionCustomUiOverlayFocusesItselfWhenClicked()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var window = new Window();
        var host = new ExtensionUiBridgeHost(window, update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) =>
                Task.FromResult(data == "\r"
                    ? new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data)
                    : new ExtensionCustomUiSnapshot(requestId, ["Pick"], width ?? 80, height ?? 24))
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);

        var other = new View { CanFocus = true };
        window.Add(other);
        other.SetFocus();
        Assert.False(overlay.HasFocus);

        var args = new MouseEventArgs
        {
            Flags = MouseFlags.Button1Clicked,
            Position = new Point(1, 1),
            ScreenPosition = new Point(1, 1),
            View = overlay
        };

        Assert.True(overlay.NewMouseEvent(args));
        Assert.True(overlay.HasFocus);

        Assert.True(overlay.HandleKeyDown(new Key(KeyCode.Enter)));
        await showTask;
    }

    [Fact]
    public async Task ExtensionCustomUiOverlaySwallowsNonPrimaryMouseEventsWithoutForwarding()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var forwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) =>
            {
                forwarded.TrySetResult(data ?? string.Empty);
                return Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data));
            }
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);

        var args = new MouseEventArgs
        {
            Flags = MouseFlags.Button3Clicked,
            Position = new Point(4, 2),
            ScreenPosition = new Point(4, 2),
            View = overlay
        };
        var handled = overlay.NewMouseEvent(args);

        Assert.True(handled);
        Assert.True(args.Handled);

        var completed = await Task.WhenAny(forwarded.Task, Task.Delay(100));
        Assert.NotSame(forwarded.Task, completed);

        Assert.False(showTask.IsCompleted);
        Assert.NotNull(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ExtensionCustomUiOverlayForwardsResizeThroughCustomUiTransport()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var forwarded = new TaskCompletionSource<(string? Data, int? Width, int? Height, string? Event)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, eventName, _) =>
            {
                forwarded.TrySetResult((data, width, height, eventName));
                return Task.FromResult(
                    data == "\r"
                        ? new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data)
                        : new ExtensionCustomUiSnapshot(requestId, ["Pick"], width ?? 80, height ?? 24));
            }
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);

        overlay.Frame = new Rectangle(0, 0, 100, 30);

        var forwardedRequest = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(forwardedRequest.Data);
        Assert.Equal(100, forwardedRequest.Width);
        Assert.Equal(30, forwardedRequest.Height);
        Assert.Equal("resize", forwardedRequest.Event);

        Assert.True(overlay.HandleKeyDown(new Key(KeyCode.Enter)));
        await showTask;
    }

    [Fact]
    public void ExtensionCustomUiOverlayRendersLatestSnapshotLines()
    {
        var snapshot = new ExtensionCustomUiSnapshot("custom-1", ["Pick", "> Alpha"], 80, 24);

        var rendered = ExtensionCustomUiSnapshotRenderer.RenderLines(snapshot, 20);

        Assert.Equal(["Pick", "> Alpha"], rendered);
    }

    [Fact]
    public void ExtensionCustomUiOverlayPreservesAnsiStyledSnapshotText()
    {
        var overlay = new ExtensionCustomUiOverlay();

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["\u001b[36mPick\u001b[39m"], 80, 24));

        Assert.Equal(["Pick"], overlay.RenderedLines);
        Assert.DoesNotContain("\u001b", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.Contains(overlay.StyledRuns, run => run.Text == "Pick" && run.Attribute == TuiTheme.GetTokenAttribute(TuiThemeToken.Accent));
    }

    [Fact]
    public void ExtensionCustomUiOverlayStripsIncompleteCsiSequences()
    {
        var overlay = new ExtensionCustomUiOverlay();

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["Start \u001b[36 After"], 80, 24));

        Assert.Equal("Start fter", overlay.RenderedLines[0]);
        Assert.DoesNotContain("\u001b", overlay.RenderedLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionCustomUiOverlayStripsOscSequences()
    {
        var overlay = new ExtensionCustomUiOverlay();

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["Start \u001b]0;Pick\u0007 After"], 80, 24));

        Assert.Equal("Start  After", overlay.RenderedLines[0]);
        Assert.DoesNotContain("Pick", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", overlay.RenderedLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionCustomUiOverlayClearsStyledRunsWhenSnapshotTurnsPlain()
    {
        var overlay = new ExtensionCustomUiOverlay();

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["\u001b[36mPick\u001b[39m"], 80, 24));
        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["Pick"], 80, 24));

        Assert.Equal(["Pick"], overlay.RenderedLines);
        Assert.DoesNotContain(overlay.StyledRuns, run => run.Attribute == TuiTheme.GetTokenAttribute(TuiThemeToken.Accent));
        Assert.All(overlay.StyledRuns, run => Assert.Equal(TuiTheme.GetTokenAttribute(TuiThemeToken.Text), run.Attribute));
    }

    [Fact]
    public void ExtensionCustomUiOverlayStripsUnsupportedCompleteCsiSequences()
    {
        var overlay = new ExtensionCustomUiOverlay();

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["\u001b[?25lCursor\u001b[?25h"], 80, 24));

        Assert.Equal(["Cursor"], overlay.RenderedLines);
        Assert.DoesNotContain("\u001b", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("l", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("h", overlay.RenderedLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionCustomUiOverlayStripsUnsupportedCsiIntermediateSequences()
    {
        var overlay = new ExtensionCustomUiOverlay();

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["\u001b[1 qText"], 80, 24));

        Assert.Equal(["Text"], overlay.RenderedLines);
        Assert.DoesNotContain("\u001b", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(" ", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("q", overlay.RenderedLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionCustomUiOverlayStripsDcsApcAndPmSequences()
    {
        var overlay = new ExtensionCustomUiOverlay();

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["A\u001bPsecret\u0007B\u001b_ignored\u001b\\C\u001b^hidden\u0007D"], 80, 24));

        Assert.Equal(["ABCD"], overlay.RenderedLines);
        Assert.DoesNotContain("\u001b", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", overlay.RenderedLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", overlay.RenderedLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionCustomUiOverlayIsFocusableAndPreservesViewBounds()
    {
        var overlay = new ExtensionCustomUiOverlay { Frame = new Rectangle(0, 0, 20, 10) };

        overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("custom-1", ["Pick", "> Alpha"], 80, 24));

        Assert.True(overlay.CanFocus);
        Assert.False(overlay.WantMousePositionReports);
        Assert.Equal(new Rectangle(0, 0, 20, 10), overlay.Frame);
    }

    [Fact]
    public async Task ShowCustomComponentAsyncCreatesFocusedPopupWindowOverlay()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var window = new Window { Frame = new Rectangle(0, 0, 120, 40) };
        var host = new ExtensionUiBridgeHost(window, update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) => Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data))
        };

        using var document = JsonDocument.Parse("{\"requestId\":\"custom-1\",\"lines\":[\"Pick\",\"> Alpha\"],\"width\":80,\"height\":24}");

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);
        var popup = Assert.IsAssignableFrom<Window>(overlay);
        Assert.True(overlay.CanFocus);
        Assert.True(overlay.HasFocus);
        Assert.True(host.HasActiveCustomUi);
        Assert.Equal(TuiTheme.PopupColorScheme, popup.ColorScheme);
        Assert.NotEqual(LineStyle.None, popup.BorderStyle);
        Assert.DoesNotContain("Fill", popup.Width?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Fill", popup.Height?.ToString() ?? string.Empty, StringComparison.Ordinal);

        Assert.True(host.TryHandleCustomUiKey(Key.Enter));

        var result = await showTask;
        Assert.True(result.Ok);
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ClearSourceAsyncRemovesOwnedCustomUiOverlay()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, _, width, height, _, _) => Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Pick"], width ?? 80, height ?? 24))
        };

        using var document = JsonDocument.Parse("{\"requestId\":\"custom-1\",\"lines\":[\"Pick\"],\"width\":80,\"height\":24}");

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);
        Assert.Equal("ext", overlay.SourceId);
        Assert.True(overlay.Visible);

        await host.ClearSourceAsync("ext");

        var result = await showTask;
        Assert.Null(host.CustomUiOverlay);
        Assert.False(overlay.Visible);
        Assert.Empty(state.BridgeSlots);
        Assert.False(result.Ok);
        Assert.Equal("Custom UI was closed.", result.Error);
    }

    [Fact]
    public async Task ShowCustomComponentAsyncCancelsActiveSessionAndRemovesOverlay()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var restoreFocusCalled = false;
        using var cts = new CancellationTokenSource();
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            RestoreFocus = () => restoreFocusCalled = true,
            SendCustomUiInputAsync = (requestId, _, width, height, _, _) => Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Pick"], width ?? 80, height ?? 24))
        };

        using var document = JsonDocument.Parse("{\"requestId\":\"custom-1\",\"lines\":[\"Pick\"],\"width\":80,\"height\":24}");

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone(), cts.Token);
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);
        Assert.True(overlay.Visible);

        await cts.CancelAsync();

        var completed = await Task.WhenAny(showTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(showTask, completed);

        var result = await showTask;
        Assert.False(result.Ok);
        Assert.Contains("cancel", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(host.CustomUiOverlay);
        Assert.False(overlay.Visible);
        Assert.Empty(state.BridgeSlots);
        Assert.True(restoreFocusCalled);
    }

    [Fact]
    public async Task ShowCustomComponentAsyncRejectsConcurrentSessions()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) => Task.FromResult(
                data == "\r"
                    ? new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: new { selected = "Alpha" })
                    : new ExtensionCustomUiSnapshot(requestId, ["Pick", "> Alpha"], width ?? 80, height ?? 24))
        };

        using var firstDocument = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick","> Alpha"],"width":80,"height":24}
        """);
        using var secondDocument = JsonDocument.Parse("""
        {"requestId":"custom-2","lines":["Pick","> Beta"],"width":80,"height":24}
        """);

        var startGate = new Barrier(2);
        Task<ExtensionUiResult> StartSessionAsync(JsonElement payload)
            => Task.Run(async () =>
            {
                startGate.SignalAndWait();
                return await host.ShowCustomComponentAsync("ext", payload);
            });

        var firstTask = StartSessionAsync(firstDocument.RootElement.Clone());
        var secondTask = StartSessionAsync(secondDocument.RootElement.Clone());

        var race = Task.WhenAny(firstTask, secondTask);
        var raceFinished = await Task.WhenAny(race, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(race, raceFinished);

        var rejectedTask = await race;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rejectedTask);

        ExtensionCustomUiOverlay? overlay = null;
        for (var attempt = 0; attempt < 100 && overlay is null; attempt++)
        {
            overlay = host.CustomUiOverlay;
            if (overlay is not null) break;

            await Task.Delay(10);
        }

        Assert.NotNull(overlay);
        Assert.True(overlay.HandleKeyDown(new Key(KeyCode.Enter)));

        var activeTask = ReferenceEquals(rejectedTask, firstTask) ? secondTask : firstTask;
        var activeFinished = await Task.WhenAny(activeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(activeTask, activeFinished);
        var activeResult = await activeTask;

        Assert.True(activeResult.Ok);
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ExtensionCustomUiOverlayForwardsKeyInputAndCompletesOnDoneSnapshot()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = (requestId, data, width, height, _, _) => Task.FromResult(
                data == "\u001b[B"
                    ? new ExtensionCustomUiSnapshot(requestId, ["Pick", "> Beta"], width ?? 80, height ?? 24)
                    : new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: new { selected = "Beta" }))
        };

        using var document = JsonDocument.Parse("{\"requestId\":\"custom-1\",\"lines\":[\"Pick\",\"> Alpha\"],\"width\":80,\"height\":24}");

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);

        Assert.True(overlay.HandleKeyDown(new Key(KeyCode.CursorDown)));
        await Task.Yield();
        Assert.Equal(["Pick", "> Beta"], overlay.RenderedLines);

        Assert.True(overlay.HandleKeyDown(new Key(KeyCode.Enter)));

        var result = await showTask;

        Assert.True(result.Ok);
        Assert.Equal("{\"selected\":\"Beta\"}", JsonSerializer.Serialize(result.Value));
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ExtensionCustomUiOverlaySerializesQueuedInputForwarding()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var firstRequest = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedRequests = 0;
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action(),
            SendCustomUiInputAsync = async (requestId, data, width, height, _, _) =>
            {
                var requestNumber = Interlocked.Increment(ref startedRequests);
                if (requestNumber == 1)
                {
                    firstRequest.TrySetResult(data);
                    await releaseFirstRequest.Task;
                    return new ExtensionCustomUiSnapshot(requestId, ["Pick", "> Beta"], width ?? 80, height ?? 24);
                }

                secondRequest.TrySetResult(data);
                return new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: data);
            }
        };

        using var document = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick","> Alpha"],"width":80,"height":24}
        """);

        var showTask = host.ShowCustomComponentAsync("ext", document.RootElement.Clone());
        await Task.Yield();

        var overlay = host.CustomUiOverlay;
        Assert.NotNull(overlay);

        Assert.True(overlay.HandleKeyDown(new Key(KeyCode.CursorDown)));
        Assert.Equal("\u001b[B", await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(overlay.HandleKeyDown(new Key(KeyCode.Enter)));
        var earlySecondRequest = await Task.WhenAny(secondRequest.Task, Task.Delay(100));
        Assert.NotSame(secondRequest.Task, earlySecondRequest);
        Assert.Equal(1, Volatile.Read(ref startedRequests));

        releaseFirstRequest.SetResult();

        Assert.Equal("\r", await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await showTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Ok);
        Assert.Equal("\r", Assert.IsType<string>(result.Value));
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task ShowCustomComponentAsyncClearsActiveSessionWhenInitialDispatchFaults()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var dispatchCount = 0;
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action =>
            {
                if (Interlocked.Increment(ref dispatchCount) == 1)
                {
                    throw new InvalidOperationException("initial ui dispatch failed");
                }

                action();
            },
            SendCustomUiInputAsync = (requestId, _, width, height, _, _) => Task.FromResult(new ExtensionCustomUiSnapshot(requestId, ["Done"], width ?? 80, height ?? 24, Completed: true, Value: new { selected = "Beta" }))
        };

        using var firstDocument = JsonDocument.Parse("""
        {"requestId":"custom-1","lines":["Pick"],"width":80,"height":24}
        """);
        using var secondDocument = JsonDocument.Parse("""
        {"requestId":"custom-2","lines":["Done"],"width":80,"height":24,"completed":true,"value":{"selected":"Beta"}}
        """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ShowCustomComponentAsync("ext", firstDocument.RootElement.Clone()));

        Assert.Contains("initial ui dispatch failed", exception.Message, StringComparison.OrdinalIgnoreCase);

        var result = await host.ShowCustomComponentAsync("ext", secondDocument.RootElement.Clone());

        Assert.True(result.Ok);
        Assert.Contains("Beta", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.Empty(state.BridgeSlots);
        Assert.Null(host.CustomUiOverlay);
    }

    [Fact]
    public async Task TuiExtensionUiRoutesInteractiveCustomRequestToBridgeHost()
    {
        var state = TuiRenderState.Empty("session-1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action()
        };
        IExtensionUi ui = new TuiExtensionUi(host);

        using var document = JsonDocument.Parse("""
        {"mode":"interactive-component","requestId":"custom-1","lines":["Pick","> Alpha"],"width":80,"height":24,"completed":true,"value":{"selected":"Alpha"}}
        """);

        var result = await ui.RequestAsync(new ExtensionUiRequest("ext", "custom", document.RootElement.Clone()), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Empty(state.BridgeSlots);
        Assert.Null(host.CustomUiOverlay);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
