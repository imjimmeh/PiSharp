using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Models;
using PiSharp.Server.Contracts;
using Xunit;

namespace PiSharp.Client.Tests;

public sealed class ClientEventReducerTests
{
    [Fact]
    public void Apply_MessageStart_AddsTranscriptRow()
    {
        var message = new AssistantMessage([new TextContent("hello")]);
        var state = ClientSessionState.Empty;

        var next = ClientEventReducer.Apply(state, Envelope(1, new AgentEvent.MessageStart(message)));

        var row = Assert.Single(next.Transcript);
        Assert.Equal($"assistant:{message.Timestamp.ToUnixTimeMilliseconds()}", row.EntryId);
        Assert.Equal("hello", row.Text);
        Assert.Equal("assistant", row.Role);
        Assert.True(row.IsPending);
        Assert.Equal(1L, next.LastAppliedSequence);
    }

    [Fact]
    public void Apply_MessageUpdate_UpsertsByEntryId()
    {
        var message = new AssistantMessage([new TextContent("hello")]);
        var updated = message with { Content = [new TextContent("hello world")] };
        var state = ClientEventReducer.Apply(ClientSessionState.Empty, Envelope(1, new AgentEvent.MessageStart(message)));

        var next = ClientEventReducer.Apply(
            state,
            Envelope(2, new AgentEvent.MessageUpdate(updated, new AssistantMessageEvent.TextDelta(updated, 0, " world"))));

        var row = Assert.Single(next.Transcript); // row count stays 1
        Assert.Equal($"assistant:{message.Timestamp.ToUnixTimeMilliseconds()}", row.EntryId);
        Assert.Equal("hello world", row.Text);
    }

    [Fact]
    public void Apply_ToolExecutionStart_AddsToolRowAndSetsBusy()
    {
        var arguments = JsonDocument.Parse("""{"cmd":"ls"}""").RootElement;
        var state = ClientSessionState.Empty;

        var next = ClientEventReducer.Apply(state, Envelope(1, new AgentEvent.ToolExecutionStart("tool-1", "bash", arguments)));

        var row = Assert.Single(next.Transcript);
        Assert.True(row.IsTool);
        Assert.Equal("tool-1", row.ToolCallId);
        Assert.Equal("bash", row.ToolName);
        Assert.True(row.IsPending);
        Assert.Contains("cmd", row.ToolArguments);
        Assert.True(next.IsBusy);
    }

    [Fact]
    public void Apply_ToolExecutionEnd_SetsResultAndClearsBusy()
    {
        var state = ClientEventReducer.Apply(
            ClientSessionState.Empty,
            Envelope(1, new AgentEvent.ToolExecutionStart("tool-1", "bash", JsonDocument.Parse("{}").RootElement)));

        var next = ClientEventReducer.Apply(
            state,
            Envelope(2, new AgentEvent.ToolExecutionEnd("tool-1", "bash", "done", IsError: false)));

        var row = Assert.Single(next.Transcript);
        Assert.Equal("done", row.ToolResult);
        Assert.False(row.ToolIsError);
        Assert.False(row.IsPending);
        Assert.False(next.IsBusy);
    }

    [Fact]
    public void Apply_AgentEnd_SetsBusyFalse()
    {
        var state = ClientEventReducer.Apply(
            ClientSessionState.Empty,
            Envelope(1, new AgentEvent.ToolExecutionStart("tool-1", "bash", JsonDocument.Parse("{}").RootElement)));

        var next = ClientEventReducer.Apply(state, Envelope(2, new AgentEvent.AgentEnd([])));

        Assert.False(next.IsBusy);
    }

    [Fact]
    public void Apply_ModelSelect_UpdatesModelDisplay()
    {
        var model = new ModelDescriptor(Provider: "anthropic", Id: "claude-sonnet", Api: "anthropic", Name: "Sonnet 4.5", ContextWindow: 200000);
        var state = ClientSessionState.Empty;

        var next = ClientEventReducer.Apply(
            state,
            EnvelopeOwn(1, new AgentHarnessOwnEvent.ModelSelect(model, null, "user")));

        Assert.Equal("Sonnet 4.5", next.ModelDisplay);
        Assert.Equal(200000L, next.ContextWindow);
    }

    [Fact]
    public void Apply_Compaction_TogglesIsCompacting()
    {
        var compacting = ClientEventReducer.Apply(
            ClientSessionState.Empty,
            EnvelopeOwn(1, new AgentHarnessOwnEvent.CompactionStart("token budget exceeded")));

        Assert.True(compacting.IsCompacting);
        Assert.True(compacting.IsBusy);

        var done = ClientEventReducer.Apply(
            compacting,
            EnvelopeOwn(2, new AgentHarnessOwnEvent.CompactionEnd("token budget exceeded", null, Aborted: false, WillRetry: false, ErrorMessage: null)));

        Assert.False(done.IsCompacting);
    }

    [Fact]
    public void Apply_UnknownEventType_AdvancesWatermarkOnly()
    {
        var state = ClientSessionState.Empty;

        // agent_start is a valid flat type the reducer does not handle.
        var next = ClientEventReducer.Apply(state, Envelope(7, new AgentEvent.AgentStart()));

        Assert.Equal(7L, next.LastAppliedSequence);
        Assert.Empty(next.Transcript);
        Assert.False(next.IsBusy);
        Assert.False(next.IsCompacting);
        Assert.Null(next.ModelDisplay);
    }

    private static ServerEventEnvelope Envelope(long sequence, AgentEvent coreEvent)
        => ServerEventEnvelope.FromFlat("srv_test", sequence, AgentSessionEvent.FromCore(coreEvent));

    private static ServerEventEnvelope EnvelopeOwn(long sequence, AgentHarnessOwnEvent ownEvent)
        => ServerEventEnvelope.FromFlat("srv_test", sequence, AgentSessionEvent.FromOwn(ownEvent));
}
