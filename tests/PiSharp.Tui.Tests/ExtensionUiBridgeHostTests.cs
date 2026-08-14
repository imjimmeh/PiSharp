using System.Reflection;
using System.Text.Json;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class ExtensionUiBridgeHostTests
{
    [Fact]
    public async Task UnknownIntentReturnsCancelledResult()
    {
        var host = new ExtensionUiBridgeHost(new Window());

        var result = await host.HandleAsync(new ExtensionUiIntent("req", "unknown", "Title", null, null, null));

        Assert.True(result.Cancelled);
        Assert.Equal("req", result.RequestId);
    }

    [Fact]
    public async Task SetWidgetPreservesRequestedPlacement()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action()
        };

        await host.SetWidgetAsync("extension:a", new ExtensionWidgetState("text", "hello", "Widget", Placement: "above-chat"));

        var slot = Assert.Single(state.BridgeSlots);
        Assert.Equal("above-chat", slot.Placement);
        Assert.Equal("extension:a", slot.SourceId);
    }

    [Fact]
    public async Task FooterIntentReplacesBuiltInFooter()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action()
        };
        var footer = new FooterView();

        await host.HandleAsync(new ExtensionUiIntent("req", "footer", "Footer", "bridge footer", null, null, "extension:a"));
        footer.Render(state, new TuiFooterSnapshot("cwd", null, 0, 0, 0, 0, 0, 0, 0, false, state.Statuses), widthOverride: 80);

        Assert.Contains("bridge footer", footer.Text?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task ExtensionUiFooterReplacesAndRestoresBuiltInFooter()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action()
        };
        IExtensionUi ui = new TuiExtensionUi(host);
        var footer = new FooterView();

        await ui.SetFooterAsync("extension:a", new ExtensionWidgetState("text", "custom footer\nsecond line", "Footer", Placement: "footer"));
        footer.Render(state, new TuiFooterSnapshot("cwd", null, 0, 0, 0, 0, 0, 0, 0, false, state.Statuses), widthOverride: 80);

        var customText = footer.Text?.ToString() ?? string.Empty;
        Assert.Contains("custom footer", customText);
        Assert.Contains("second line", customText);
        Assert.DoesNotContain("tools:none", customText);

        await ui.SetFooterAsync("extension:a", null);
        footer.Render(state, new TuiFooterSnapshot("cwd", null, 0, 0, 0, 0, 0, 0, 0, false, state.Statuses), widthOverride: 80);

        Assert.Contains("tools:none", footer.Text?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task NotifyRestoresPromptFocusAfterModalNotificationCloses()
    {
        var restoredFocus = false;
        string? notification = null;
        var host = new ExtensionUiBridgeHost(new Window());
        SetHostProperty(host, "RestoreFocus", (Action)(() => restoredFocus = true));
        SetHostProperty(host, "DispatchUi", (Action<Action>)(action => action()));
        SetHostProperty(host, "ShowNotification", (Action<string>)(message => notification = message));

        await host.NotifyAsync("loaded");

        Assert.Equal("loaded", notification);
        Assert.True(restoredFocus);
    }

    [Fact]
    public async Task UpdatesAndCleansExtensionOwnedUiState()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var editor = string.Empty;
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), () => editor, text => editor = text)
        {
            DispatchUi = action => action()
        };

        await host.SetStatusAsync("extension:a", "ready");
        await host.SetWidgetAsync("extension:a", new ExtensionWidgetState("text", "hello", "Widget"));
        await host.SetTitleAsync("Title");
        await host.SetEditorTextAsync("draft");
        await host.SetWorkingMessageAsync("Crunching...");
        await host.SetWorkingVisibleAsync(false);
        await host.SetWorkingIndicatorAsync(new ExtensionWorkingIndicator(Spinner: "●"));

        Assert.Equal("ready", state.Statuses["extension:a"]);
        Assert.Single(state.BridgeSlots);
        Assert.Equal("Title", state.TitleOverride);
        Assert.Equal("draft", await host.GetEditorTextAsync());
        Assert.Equal("Crunching...", state.WorkingMessage);
        Assert.False(state.WorkingVisible);
        Assert.Equal("●", state.WorkingIndicator?.Spinner);

        await host.ClearSourceAsync("extension:a");

        Assert.Empty(state.BridgeSlots);
        Assert.Empty(state.Statuses);
    }

    [Fact]
    public async Task ShowCustomComponentAsync_does_not_throw_already_active_on_sequential_calls()
    {
        // Arrange: host with synchronous UI dispatch
        var host = new ExtensionUiBridgeHost(new Window())
        {
            DispatchUi = action => action()
        };

        var payload = JsonSerializer.SerializeToElement(new
        {
            requestId = "test-session-1",
            completed = false,
            lines = Array.Empty<string>()
        });

        // Act: first call with pre-cancelled token — triggers CancelCustomUiSession
        // which calls CompleteCustomUiSession, clearing _customUiCompletion.
        using var cts1 = new CancellationTokenSource();
        cts1.Cancel();
        try
        {
            await host.ShowCustomComponentAsync("test-ext", payload, cts1.Token);
        }
        catch
        {
            // May throw if TUI components fail in test environment.
            // Either way, _customUiCompletion must have been cleared.
        }

        // Assert: second call must NOT throw "A custom UI session is already active"
        var payload2 = JsonSerializer.SerializeToElement(new
        {
            requestId = "test-session-2",
            completed = false,
            lines = Array.Empty<string>()
        });
        using var cts2 = new CancellationTokenSource();
        cts2.Cancel();

        var exception = await Record.ExceptionAsync(async () =>
            await host.ShowCustomComponentAsync("test-ext", payload2, cts2.Token));

        Assert.True(
            exception is null || exception is not InvalidOperationException { Message: "A custom UI session is already active." },
            $"Second ShowCustomComponentAsync call must not see 'already active' guard; got: {exception?.Message}");
    }

    [Fact]
    public async Task ForwardCustomUiInputAsync_completes_old_session_even_when_overlay_replaced_by_new_session()
    {
        // Arrange
        var host = new ExtensionUiBridgeHost(new Window())
        {
            DispatchUi = action => action()
        };

        // The "gate" lets the test pause ForwardCustomUiInputAsync in the async gap
        // (after SendCustomUiInputAsync is called but before InvokeOnUiThreadAsync).
        var inputRpcGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var session1Tcs = new TaskCompletionSource<ExtensionUiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session2Tcs = new TaskCompletionSource<ExtensionUiResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Inject session-1 state via reflection
        var type = typeof(ExtensionUiBridgeHost);
        var overlayField = type.GetField("_customUiOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var completionField = type.GetField("_customUiCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var session1Overlay = new ExtensionCustomUiOverlay();
        session1Overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("s1", [], 80, 24));
        overlayField.SetValue(host, session1Overlay);
        completionField.SetValue(host, session1Tcs);

        // Wire SendCustomUiInputAsync: wait for the test to open the gate, then return completed=true
        host.SendCustomUiInputAsync = async (requestId, data, w, h, ev, ct) =>
        {
            await inputRpcGate.Task; // pause here — test injects session-2 now
            return new ExtensionCustomUiSnapshot(requestId, [], w ?? 80, h ?? 24, Completed: true);
        };

        // Act: fire ForwardCustomUiInputAsync (will pause at gate)
        var forwardTask = InvokeForwardCustomUiInputAsync(host, "\u001b", CancellationToken.None);

        // Give ForwardCustomUiInputAsync time to reach the gate
        await Task.Delay(50);

        // Simulate the race: session-2 starts, replacing overlay and completion
        var session2Overlay = new ExtensionCustomUiOverlay();
        session2Overlay.UpdateSnapshot(new ExtensionCustomUiSnapshot("s2", [], 80, 24));
        overlayField.SetValue(host, session2Overlay);
        completionField.SetValue(host, session2Tcs);

        // Release the gate — ForwardCustomUiInputAsync resumes with completed=true
        inputRpcGate.SetResult();

        await forwardTask;

        // Assert: session-1's TCS is resolved (old ui_request gets its response)
        Assert.True(session1Tcs.Task.IsCompleted, "Session 1 TCS must be resolved even when overlay was replaced");
        var session1Result = await session1Tcs.Task;
        Assert.True(session1Result.Ok);

        // Assert: session-2's TCS is NOT touched
        Assert.False(session2Tcs.Task.IsCompleted, "Session 2 TCS must NOT be resolved by session 1's completion");
    }

    [Fact]
    public async Task GetToolsExpandedAsync_ReadsCurrentStateDefault()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };

        Assert.False(await host.GetToolsExpandedAsync());
        state = state.SetToolOutput(true);
        Assert.True(await host.GetToolsExpandedAsync());
    }

    [Fact]
    public async Task SetToolsExpandedAsync_UpdatesShowToolOutput()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };

        await host.SetToolsExpandedAsync(true);

        Assert.True(state.ShowToolOutput);
    }

    [Fact]
    public async Task SetEditorComponentAsync_UpsertsEditorSlot()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };

        await host.SetEditorComponentAsync("ext-a", new ExtensionWidgetState("text", "editor body", "Ext", Placement: "editor"));

        var slot = Assert.Single(state.BridgeSlots);
        Assert.Equal("editor:ext-a", slot.Id);
        Assert.Equal("editor body", slot.Content);
    }

    [Fact]
    public async Task GetEditorComponentAsync_ReturnsStoredComponentAndClearRestoresPrompt()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };

        await host.SetEditorComponentAsync("ext-a", new ExtensionWidgetState("text", "editor body", "Ext", Placement: "editor"));
        var stored = await host.GetEditorComponentAsync("ext-a");

        Assert.NotNull(stored);
        Assert.Equal("editor body", stored.Content);

        await host.SetEditorComponentAsync("ext-a", null);
        Assert.Null(await host.GetEditorComponentAsync("ext-a"));
        Assert.Empty(state.BridgeSlots);
    }

    [Fact]
    public async Task GetEditorComponentAsync_ReturnsNullWhenUnset()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };

        Assert.Null(await host.GetEditorComponentAsync("ext-a"));
    }

    private static Task InvokeForwardCustomUiInputAsync(ExtensionUiBridgeHost host, string data, CancellationToken ct)
    {
        var method = typeof(ExtensionUiBridgeHost)
            .GetMethod("ForwardCustomUiInputAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(host, [data, ct])!;
    }

    private static void SetHostProperty(ExtensionUiBridgeHost host, string name, object value)
    {
        var property = typeof(ExtensionUiBridgeHost).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property.SetValue(host, value);
    }
}
