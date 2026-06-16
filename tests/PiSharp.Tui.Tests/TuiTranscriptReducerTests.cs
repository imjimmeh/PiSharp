using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiTranscriptReducerTests
{
    [Fact]
    public void AppendSystemCanPinExpireAndClearLocalRows()
    {
        var state = Empty();

        state = TuiTranscriptReducer.AppendSystem(state, "sticky");
        state = TuiTranscriptReducer.AppendSystem(state, "top", pinToTop: true);
        state = TuiTranscriptReducer.AppendSystem(state, "short", expiresAfter: TimeSpan.FromMilliseconds(1));

        Assert.Equal(["top", "sticky", "short"], state.Transcript.Select(item => item.Text));
        Assert.True(state.Transcript[0].IsPinnedToTop);

        var withoutExpired = TuiTranscriptReducer.RemoveExpiredSystemRows(state, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.DoesNotContain(withoutExpired.Transcript, item => item.Text == "short");
        Assert.Empty(TuiTranscriptReducer.ClearTranscript(withoutExpired).Transcript);
    }

    [Fact]
    public void RestoreLocalSystemRowsKeepsPinnedRowsBeforeHydratedRows()
    {
        var previous = Empty();
        previous = TuiTranscriptReducer.AppendSystem(previous, "pinned", pinToTop: true);
        previous = TuiTranscriptReducer.AppendSystem(previous, "local");
        var hydrated = Empty() with { Transcript = [new TuiTranscriptItem("user", "hello", EntryId: "u1")] };

        var restored = TuiTranscriptReducer.RestoreLocalSystemRows(hydrated, previous.Transcript);

        Assert.Equal(["pinned", "hello", "local"], restored.Transcript.Select(item => item.Text));
    }

    [Fact]
    public void RemoveLocalSystemRowRemovesOnlyKeyedRow()
    {
        var state = Empty();
        state = TuiTranscriptReducer.AppendSystem(state, "loading", localId: "test-loading");
        state = TuiTranscriptReducer.AppendSystem(state, "sticky");

        var result = TuiTranscriptReducer.RemoveLocalSystemRow(state, "test-loading");

        Assert.DoesNotContain(result.Transcript, item => item.Text == "loading");
        Assert.Contains(result.Transcript, item => item.Text == "sticky");
    }

    [Fact]
    public void RemoveLocalSystemRowPreservesUnrelatedLocalRows()
    {
        var state = Empty();
        state = TuiTranscriptReducer.AppendSystem(state, "sticky warning", isError: true);
        state = TuiTranscriptReducer.AppendSystem(state, "loading", localId: "test-loading");

        var result = TuiTranscriptReducer.RemoveLocalSystemRow(state, "test-loading");

        Assert.Contains(result.Transcript, item => item.Text == "sticky warning");
        Assert.DoesNotContain(result.Transcript, item => item.Text == "loading");
    }

    [Fact]
    public void AppendSystemAcceptsTagAndDelay()
    {
        var state = Empty();
        state = TuiTranscriptReducer.AppendSystem(state, "tagged-msg",
            systemMessageTag: "test-event",
            removeDelayAfterEvent: TimeSpan.FromSeconds(3));

        var item = state.Transcript[0];
        Assert.Equal("tagged-msg", item.Text);
        Assert.Equal("test-event", item.SystemMessageTag);
        Assert.Equal(TimeSpan.FromSeconds(3), item.RemoveDelayAfterEvent);
        Assert.Null(item.ExpiresAt);
    }

    [Fact]
    public void TriggerSystemMessageEventSetsExpiryOnMatchingTag()
    {
        var state = Empty();
        state = TuiTranscriptReducer.AppendSystem(state, "msg-a", systemMessageTag: "evt-a");
        state = TuiTranscriptReducer.AppendSystem(state, "msg-b", systemMessageTag: "evt-b");
        state = TuiTranscriptReducer.AppendSystem(state, "untagged");

        var now = DateTimeOffset.UtcNow;
        state = TuiTranscriptReducer.TriggerSystemMessageEvent(state, "evt-a", now);

        var msgA = state.Transcript.Single(i => i.Text == "msg-a");
        var msgB = state.Transcript.Single(i => i.Text == "msg-b");
        var untagged = state.Transcript.Single(i => i.Text == "untagged");

        Assert.NotNull(msgA.ExpiresAt);
        Assert.Null(msgB.ExpiresAt);
        Assert.Null(untagged.ExpiresAt);
    }

    [Fact]
    public void TriggerSystemMessageEventRespectsDelay()
    {
        var state = Empty();
        state = TuiTranscriptReducer.AppendSystem(state, "delayed",
            systemMessageTag: "evt",
            removeDelayAfterEvent: TimeSpan.FromSeconds(5));

        var now = DateTimeOffset.UtcNow;
        state = TuiTranscriptReducer.TriggerSystemMessageEvent(state, "evt", now);

        var item = state.Transcript[0];
        Assert.NotNull(item.ExpiresAt);
        Assert.True(item.ExpiresAt! >= now.Add(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void EventTriggerPlusCleanupTimerRemovesAbortMessages()
    {
        var state = Empty();
        state = TuiTranscriptReducer.AppendSystem(state, "Abort requested.",
            systemMessageTag: "abort",
            removeDelayAfterEvent: TimeSpan.FromSeconds(2));
        state = TuiTranscriptReducer.AppendSystem(state, "sticky");

        var now = DateTimeOffset.UtcNow;
        state = TuiTranscriptReducer.TriggerSystemMessageEvent(state, "abort", now);

        Assert.Equal(2, state.Transcript.Count);

        var cleanupTime = now.Add(TimeSpan.FromSeconds(2)).AddTicks(1);
        state = TuiTranscriptReducer.RemoveExpiredSystemRows(state, cleanupTime);

        Assert.Single(state.Transcript);
        Assert.Equal("sticky", state.Transcript[0].Text);
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
}
