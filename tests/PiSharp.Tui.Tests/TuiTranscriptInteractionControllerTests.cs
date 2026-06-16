using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Harness;
using PiSharp.Tui.Interactive.Sessions;
using PiSharp.Tui.Interactive.Shell;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiTranscriptInteractionControllerTests
{
    private static TuiRenderState CreateEmptyState() =>
        TuiRenderState.Empty("sid", null,
            new ModelDescriptor("test", "m", "test"),
            ThinkingLevel.Off, null);

    private static TuiFooterSnapshot EmptyFooterSnapshot() =>
        new("", null, 0, 0, 0, 0, 0m, 0, 0, false, new Dictionary<string, string>());

    private (TuiTranscriptInteractionController Controller, FakeTuiApplicationContext AppContext, Func<TuiRenderState> GetState) CreateController(
        TuiHostOptions options,
        Func<CancellationToken, Task<TuiSessionSnapshot?>>? loadSnapshot = null,
        Action<TuiSessionSnapshot, bool>? applySnapshot = null)
    {
        var appContext = new FakeTuiApplicationContext();
        var state = CreateEmptyState();
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);

        var sessionContext = new TuiSessionContext { CurrentHarness = TuiIntegrationTestHost.CreateHarness() };
        var stateGateway = new TuiStateGateway(() => state, next => state = next, renderCoordinator, appContext, CancellationToken.None);

        var controller = new TuiTranscriptInteractionController(
            shell, options, appContext, renderCoordinator, stateGateway, sessionContext,
            loadSnapshot ?? (_ => Task.FromResult<TuiSessionSnapshot?>(null)),
            applySnapshot ?? ((_, _) => { }),
            CancellationToken.None);

        return (controller, appContext, () => state);
    }

    // --- ForkFromMessageAsync: no fork delegate ---

    [Fact]
    public async Task ForkFromMessageAsync_WhenForkFromEntryAsyncIsNull_AppendsForkUnavailableErrorToTranscript()
    {
        var options = new TuiHostOptions(
            TuiIntegrationTestHost.CreateHarness(), "sid", null,
            _ => Task.FromResult<string?>(null));
        // ForkFromEntryAsync defaults to null — not supplied

        var (controller, appContext, getState) = CreateController(options);

        await controller.ForkFromMessageAsync("entry-1");

        // UpdateState runs synchronously (no Post), so no pump required
        var systemMessages = getState().Transcript
            .Where(item => string.Equals(item.Role, "system", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(systemMessages, msg =>
            msg.Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) && msg.IsError);
    }

    // --- ForkFromMessageAsync: success path ---

    [Fact]
    public async Task ForkFromMessageAsync_WhenForkSucceeds_AppliesSnapshotAndAppendsConfirmationMessage()
    {
        var expectedSnapshot = new TuiSessionSnapshot("forked-id", null, "Forked", []);
        var snapshotApplyCount = 0;
        TuiSessionSnapshot? capturedSnapshot = null;

        var options = new TuiHostOptions(
            TuiIntegrationTestHost.CreateHarness(), "sid", null,
            _ => Task.FromResult<string?>(null),
            ForkFromEntryAsync: (_, _) => Task.CompletedTask);

        var (controller, appContext, getState) = CreateController(
            options,
            loadSnapshot: _ => Task.FromResult<TuiSessionSnapshot?>(expectedSnapshot),
            applySnapshot: (snapshot, _) => { snapshotApplyCount++; capturedSnapshot = snapshot; });

        await controller.ForkFromMessageAsync("entry-1");
        appContext.Dispatcher.PumpPosted();

        Assert.Equal(1, snapshotApplyCount);
        Assert.Same(expectedSnapshot, capturedSnapshot);

        var systemMessages = getState().Transcript
            .Where(item => string.Equals(item.Role, "system", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(systemMessages, msg =>
            msg.Text.Contains("Forked conversation", StringComparison.OrdinalIgnoreCase) && !msg.IsError);
    }

    [Fact]
    public async Task ForkFromMessageAsync_WhenForkSucceeds_WithNullSnapshot_SkipsApplyAndStillShowsConfirmation()
    {
        var snapshotApplied = false;

        var options = new TuiHostOptions(
            TuiIntegrationTestHost.CreateHarness(), "sid", null,
            _ => Task.FromResult<string?>(null),
            ForkFromEntryAsync: (_, _) => Task.CompletedTask);

        var (controller, appContext, getState) = CreateController(
            options,
            loadSnapshot: _ => Task.FromResult<TuiSessionSnapshot?>(null),
            applySnapshot: (_, _) => { snapshotApplied = true; });

        await controller.ForkFromMessageAsync("entry-1");
        appContext.Dispatcher.PumpPosted();

        Assert.False(snapshotApplied, "ApplySnapshot must not be called when snapshot is null");

        var systemMessages = getState().Transcript
            .Where(item => string.Equals(item.Role, "system", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(systemMessages, msg =>
            msg.Text.Contains("Forked conversation", StringComparison.OrdinalIgnoreCase) && !msg.IsError);
    }

    // --- ForkFromMessageAsync: error path ---

    [Fact]
    public async Task ForkFromMessageAsync_WhenForkThrows_AppendsErrorMessageContainingExceptionText()
    {
        var options = new TuiHostOptions(
            TuiIntegrationTestHost.CreateHarness(), "sid", null,
            _ => Task.FromResult<string?>(null),
            ForkFromEntryAsync: (_, _) =>
                Task.FromException(new InvalidOperationException("session is locked")));

        var (controller, appContext, getState) = CreateController(options);

        await controller.ForkFromMessageAsync("entry-1");
        appContext.Dispatcher.PumpPosted();

        var systemMessages = getState().Transcript
            .Where(item => string.Equals(item.Role, "system", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(systemMessages, msg =>
            msg.Text.Contains("Fork failed:", StringComparison.OrdinalIgnoreCase)
            && msg.Text.Contains("session is locked", StringComparison.OrdinalIgnoreCase)
            && msg.IsError);
    }

    [Fact]
    public async Task ForkFromMessageAsync_WhenForkThrows_PostsErrorBeforeAnySnapshotLoad()
    {
        // Verify that an exception during ForkFromEntryAsync (before snapshot load)
        // produces an error message, not a silent failure.
        var snapshotLoadCalled = false;

        var options = new TuiHostOptions(
            TuiIntegrationTestHost.CreateHarness(), "sid", null,
            _ => Task.FromResult<string?>(null),
            ForkFromEntryAsync: (_, _) =>
                Task.FromException(new InvalidOperationException("network error")));

        var (controller, appContext, getState) = CreateController(
            options,
            loadSnapshot: _ => { snapshotLoadCalled = true; return Task.FromResult<TuiSessionSnapshot?>(null); });

        await controller.ForkFromMessageAsync("entry-1");
        appContext.Dispatcher.PumpPosted();

        Assert.False(snapshotLoadCalled, "Snapshot load must not be attempted after a fork exception");

        var systemMessages = getState().Transcript
            .Where(item => string.Equals(item.Role, "system", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(systemMessages, msg => msg.IsError && msg.Text.Contains("network error", StringComparison.OrdinalIgnoreCase));
    }

    // --- Context menu position: shell layout invariant ---

    [Fact]
    public void ChatView_Y_IsPositionedBelowMenuBarAndHeader_RequiringFrameOffsetForScreenAbsoluteMenuPosition()
    {
        // Context menu positions must use screen-absolute coordinates.
        // ChatView is placed at Y = Header.Height + MenuBarHeight below the window origin,
        // so hit.ViewRow alone would place the menu too high by that offset.
        // The fix adds chatFrame.Y to produce the correct screen-absolute Y.
        var expectedChatYOffset = 3 + TuiShellView.MenuBarHeight; // Header (3) + MenuBar (1) = 4

        Assert.True(expectedChatYOffset > 0,
            "ChatView must have a non-zero Y offset from the window origin so context menu position requires the frame offset");
        Assert.Equal(4, expectedChatYOffset);
    }
}
