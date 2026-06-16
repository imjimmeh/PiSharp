using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiSessionSwitchTests
{
    [Fact]
    public void ApplyingNewSnapshotClearsPreviousSessionMessagesButKeepsLocalSystemRows()
    {
        var previous = Empty()
            .HydrateSession("old", "old.jsonl", null,
            [
                new MessageEntry { Id = "old-user", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("old visible message") }
            ])
            .AppendSystem("Switched to session new.");
        var snapshot = new TuiSessionSnapshot(
            "new",
            "new.jsonl",
            null,
            [new MessageEntry { Id = "new-user", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("new visible message") }]);

        var next = TuiSessionSwitch.ApplySnapshot(previous, snapshot, preserveLocalSystemRows: true);

        Assert.DoesNotContain(next.Transcript, item => item.Text.Contains("old visible message", StringComparison.Ordinal));
        Assert.Contains(next.Transcript, item => item.Text.Contains("new visible message", StringComparison.Ordinal));
        Assert.Contains(next.Transcript, item => item.Text.Contains("Switched to session new.", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyingNewSnapshotDropsTransientLocalSystemRows()
    {
        var previous = Empty()
            .AppendSystem("Switched to session new.", expiresAfter: TimeSpan.FromSeconds(6))
            .AppendSystem("Sticky warning", isError: true);
        var snapshot = new TuiSessionSnapshot(
            "new",
            "new.jsonl",
            null,
            [new MessageEntry { Id = "new-user", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("new visible message") }]);

        var next = TuiSessionSwitch.ApplySnapshot(previous, snapshot, preserveLocalSystemRows: true);

        Assert.DoesNotContain(next.Transcript, item => item.Text.Contains("Switched to session new.", StringComparison.Ordinal));
        Assert.Contains(next.Transcript, item => item.Text.Contains("Sticky warning", StringComparison.Ordinal));
    }

    [Fact]
    public void ResumeLoadingStateShowsImmediateBusyFeedback()
    {
        var state = TuiSessionSwitch.BeginResumeLoading(Empty());

        Assert.True(state.IsBusy);
        Assert.Equal("Loading sessions", state.Status);
        Assert.Equal("Loading sessions...", state.WorkingMessage);
        Assert.Contains(state.Transcript, item => item.Role == "system" && item.Text.Contains("Loading sessions", StringComparison.Ordinal));
    }

    [Fact]
    public void EndCommandLoadingRemovesLoadingSystemRowAndClearsBusyStatus()
    {
        var state = TuiSessionSwitch.BeginResumeLoading(Empty())
            .AppendSystem("Sticky warning", isError: true);

        var result = TuiSessionSwitch.EndCommandLoading(state);

        Assert.False(result.IsBusy);
        Assert.Equal("Idle", result.Status);
        Assert.Null(result.WorkingMessage);
        Assert.DoesNotContain(result.Transcript, item => item.Text.Contains("Loading sessions", StringComparison.Ordinal));
        Assert.Contains(result.Transcript, item => item.Text == "Sticky warning");
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
}
