using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive.Components;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiRenderStateTests
{
    [Fact]
    public void ReducesMessageStreamingAndFinalization()
    {
        var state = Empty();
        var partial = AgentMessages.Assistant("hel");
        var final = AgentMessages.Assistant("hello");

        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageStart(partial)));
        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(final)));

        var item = Assert.Single(state.Transcript);
        Assert.Equal("assistant", item.Role);
        Assert.Equal("hello", item.Text);
        Assert.False(item.IsStreaming);
    }

    [Fact]
    public void ReducesAssistantErrorWithoutContent()
    {
        var state = Empty();
        var message = new AssistantMessage([], StopReason: "error", ErrorMessage: "provider failed");

        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(message)));

        var item = Assert.Single(state.Transcript);
        Assert.Equal("assistant", item.Role);
        Assert.True(item.IsError);
        Assert.Equal("provider failed", item.Text);
        Assert.Contains(TuiMessageRenderer.Render(item, state), row => row.Text.Contains("✗ provider failed", StringComparison.Ordinal));
    }

    [Fact]
    public void ReducesToolRows()
    {
        using var args = JsonDocument.Parse("{}");
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tool-1", "read", new { ok = true }, false)));

        var item = Assert.Single(state.Transcript);
        Assert.Equal("tool-1", item.ToolCallId);
        Assert.Equal("read", item.ToolName);
        Assert.False(item.IsStreaming);
        Assert.Contains("ok", item.Text);
    }

    [Fact]
    public void ReducesToolRowsByToolCallId()
    {
        using var args = JsonDocument.Parse("{}");
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tool-1", "read", new { ok = true }, false)));

        var item = Assert.Single(state.Transcript);
        Assert.Equal("tool-1", item.ToolCallId);
        Assert.Equal("read", item.ToolName);
        Assert.False(item.IsStreaming);
        Assert.Contains("ok", item.Text);
        Assert.DoesNotContain("{}", ToolExecutionView.Render(item, false)[1]);
        Assert.Contains("read", ToolExecutionView.Render(item, false)[1]);
    }

    [Fact]
    public void AssistantToolCallsAndResultsShareOneTranscriptRow()
    {
        using var args = JsonDocument.Parse("{\"path\":\"file.txt\"}");
        var assistant = new AssistantMessage([new ToolCallContent("tool-1", "read", args.RootElement.Clone())]);
        var result = AgentMessages.ToolResult("tool-1", "read", "file contents");

        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageUpdate(assistant, default!)))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tool-1", "read", "file contents", false)))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(result)));

        var item = Assert.Single(state.Transcript);
        Assert.Equal("tool", item.Role);
        Assert.Equal("tool-1", item.ToolCallId);
        Assert.Equal("read", item.ToolName);
        Assert.Equal("file contents", item.Text);
        Assert.False(item.IsStreaming);
        Assert.Contains("file.txt", ToolExecutionView.Render(item, false)[1]);
    }

    [Fact]
    public void StreamingToolPlaceholderIsUpdatedInPlace()
    {
        using var emptyArgs = JsonDocument.Parse("{}");
        using var actualArgs = JsonDocument.Parse("{\"action\":\"update\",\"id\":1}");
        var placeholder = new AssistantMessage([new ToolCallContent(string.Empty, string.Empty, emptyArgs.RootElement.Clone())]);
        var actual = new AssistantMessage([new ToolCallContent("tool-1", "todo", actualArgs.RootElement.Clone())]);

        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageUpdate(placeholder, default!)))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageUpdate(placeholder, default!)))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageUpdate(actual, default!)))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "todo", actualArgs.RootElement.Clone())))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tool-1", "todo", "Updated #1", false)));

        var item = Assert.Single(state.Transcript);
        Assert.Equal("tool-1", item.ToolCallId);
        Assert.Equal("todo", item.ToolName);
        Assert.Equal("Updated #1", item.Text);
        Assert.False(item.IsStreaming);
        Assert.Contains("action", ToolExecutionView.Render(item, false)[1]);
    }

    [Fact]
    public void SetToolRenderedLinesUpdatesMatchingToolByToolCallId()
    {
        using var args = JsonDocument.Parse("{\"action\":\"create\"}");
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "todo", args.RootElement.Clone())))
            .SetToolRenderedLines("tool-1", ["todo + Audit extension styling"], ["○ pending"]);

        var item = Assert.Single(state.Transcript);
        Assert.Equal(["todo + Audit extension styling"], item.RenderedToolCall);
        Assert.Equal(["○ pending"], item.RenderedToolResult);
    }

    [Fact]
    public void ToggleToolExpandedFlipsMatchingToolByToolCallId()
    {
        using var args = JsonDocument.Parse("{}");
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-2", "write", args.RootElement.Clone())));

        var toggled = state.ToggleToolExpanded("tool-1");

        Assert.True(toggled.Transcript.Single(item => item.ToolCallId == "tool-1").IsExpanded);
        Assert.False(toggled.Transcript.Single(item => item.ToolCallId == "tool-2").IsExpanded);

        var untoggled = toggled.ToggleToolExpanded("tool-1");

        Assert.False(untoggled.Transcript.Single(item => item.ToolCallId == "tool-1").IsExpanded);
    }

    [Fact]
    public void ToolExpandedStateSurvivesLifecycleUpdates()
    {
        using var args = JsonDocument.Parse("{}");
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .ToggleToolExpanded("tool-1")
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionUpdate("tool-1", "read", args.RootElement.Clone(), "partial")))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tool-1", "read", "done", false)));

        var item = Assert.Single(state.Transcript);
        Assert.True(item.IsExpanded);
        Assert.Equal("tool-1", item.ToolCallId);
        Assert.Contains("done", item.Text);
    }

    [Fact]
    public void ReducesToolUpdatesInPlace()
    {
        using var args = JsonDocument.Parse("{}");
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionUpdate("tool-1", "read", args.RootElement.Clone(), "partial")));

        var item = Assert.Single(state.Transcript);
        Assert.True(item.IsStreaming);
        Assert.Contains("partial", item.Text);
    }

    [Fact]
    public void ReducesModelAndThinkingUpdates()
    {
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ModelSelect(new ModelDescriptor("openai", "gpt-4o", "openai", Name: "GPT"), null, "test")))
            .Reduce(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ThinkingLevelSelect(ThinkingLevel.High, ThinkingLevel.Off)));

        Assert.Equal("GPT", state.ModelDisplay);
        Assert.Equal(ThinkingLevel.High, state.ThinkingLevel);
    }

    [Fact]
    public void HydrateSessionAttachesEntryIdsToMessagesAndToolRows()
    {
        using var args = JsonDocument.Parse("{\"path\":\"file.txt\"}");
        var branch = new SessionTreeEntry[]
        {
            new MessageEntry { Id = "u1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") },
            new MessageEntry { Id = "a1", ParentId = "u1", Timestamp = DateTimeOffset.UtcNow, Message = new AssistantMessage([new ToolCallContent("tool-1", "read", args.RootElement.Clone())]) },
            new MessageEntry { Id = "t1", ParentId = "a1", Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.ToolResult("tool-1", "read", "contents") }
        };

        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())))
            .ToggleToolExpanded("tool-1")
            .HydrateSession("fork", "fork.jsonl", "Fork", branch);

        Assert.Equal("fork", state.SessionId);
        Assert.Equal("fork.jsonl", state.SessionFile);
        Assert.Equal("Fork", state.SessionName);
        Assert.Contains(state.Transcript, item => item.Role == "user" && item.EntryId == "u1");
        var tool = Assert.Single(state.Transcript, item => item.ToolCallId == "tool-1");
        Assert.Equal("t1", tool.EntryId);
        Assert.True(tool.IsExpanded);
    }

    [Fact]
    public void PinnedSystemRowsRenderBeforeHydratedTranscript()
    {
        var branch = new SessionTreeEntry[]
        {
            new MessageEntry { Id = "u1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") }
        };

        var state = Empty()
            .HydrateSession("sid", "session.jsonl", null, branch)
            .AppendSystem("Loaded extensions: test", pinToTop: true);

        Assert.Equal(["system", "user"], state.Transcript.Select(item => item.Role));
        Assert.Equal("Loaded extensions: test", state.Transcript[0].Text);
        Assert.True(state.Transcript[0].IsPinnedToTop);
    }

    [Fact]
    public void RestoreLocalSystemRowsKeepsPinnedRowsAboveHydratedTranscript()
    {
        var branch = new SessionTreeEntry[]
        {
            new MessageEntry { Id = "u1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") }
        };
        var previous = Empty()
            .AppendSystem("Loaded extensions: test", pinToTop: true)
            .AppendSystem("Request aborted.");

        var state = Empty()
            .HydrateSession("sid", "session.jsonl", null, branch)
            .RestoreLocalSystemRows(previous.Transcript);

        Assert.Equal(["system", "user", "system"], state.Transcript.Select(item => item.Role));
        Assert.Equal("Loaded extensions: test", state.Transcript[0].Text);
        Assert.True(state.Transcript[0].IsPinnedToTop);
        Assert.Equal("Request aborted.", state.Transcript[^1].Text);
        Assert.False(state.Transcript[^1].IsPinnedToTop);
    }

    [Fact]
    public void RemovesExpiredLocalSystemRows()
    {
        var state = Empty()
            .AppendSystem("short-lived", expiresAfter: TimeSpan.FromMilliseconds(1))
            .AppendSystem("sticky")
            .AppendSystem("error", isError: true);

        var next = state.RemoveExpiredSystemRows(DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.DoesNotContain(next.Transcript, item => item.Text == "short-lived");
        Assert.Contains(next.Transcript, item => item.Text == "sticky");
        Assert.Contains(next.Transcript, item => item.Text == "error");
    }

    [Fact]
    public void UpsertsBridgeSlotsById()
    {
        var state = Empty()
            .UpsertBridgeSlot(new TuiBridgeSlot("status", "status", "Status", "One"))
            .UpsertBridgeSlot(new TuiBridgeSlot("status", "status", "Status", "Two"));

        var slot = Assert.Single(state.BridgeSlots);
        Assert.Equal("Two", slot.Content);
    }

    [Fact]
    public void AppendsAndClearsLocalSystemRows()
    {
        var state = Empty().AppendSystem("Use /help");

        var item = Assert.Single(state.Transcript);
        Assert.Equal("system", item.Role);
        Assert.Equal("Use /help", item.Text);

        Assert.Empty(state.ClearTranscript().Transcript);
    }

    [Fact]
    public void TracksRicherTranscriptKindsSlotsAndToggles()
    {
        var message = new AssistantMessage([new ThinkingContent("chain"), new ToolCallContent("call-1", "read", JsonDocument.Parse("{}").RootElement.Clone())]);
        var state = Empty()
            .Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(message)))
            .ToggleThinking()
            .ToggleToolOutput()
            .UpsertBridgeSlot(new TuiBridgeSlot("slot", "widget", "Widget", "body", SourceId: "extension:a"))
            .SetExtensionStatus("extension:a", "ready");

        Assert.True(state.ShowThinking);
        Assert.True(state.ShowToolOutput);
        Assert.Equal("ready", state.Statuses["extension:a"]);
        Assert.Contains(state.Transcript[0].Content!, content => content is ThinkingContent);
        Assert.Empty(state.RemoveBridgeSlotsBySource("extension:a").BridgeSlots);
    }

    [Fact]
    public void ReducePreservesPendingMessageCountOnNonAgentEndEvents()
    {
        var state = Empty() with { PendingMessageCount = 3 };

        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.AgentStart()));
        Assert.Equal(3, state.PendingMessageCount);

        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.TurnStart()));
        Assert.Equal(3, state.PendingMessageCount);

        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(null, [])));
        Assert.Equal(3, state.PendingMessageCount);

        var message = AgentMessages.User("hello");
        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(message)));
        Assert.Equal(3, state.PendingMessageCount);

        using var args = JsonDocument.Parse("{}");
        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tool-1", "read", args.RootElement.Clone())));
        Assert.Equal(3, state.PendingMessageCount);
    }

    [Fact]
    public void ReduceResetsPendingMessageCountOnAgentEnd()
    {
        var state = Empty() with { PendingMessageCount = 5 };

        state = state.Reduce(new AgentHarnessEvent.Core(new AgentEvent.AgentEnd([])));

        Assert.Equal(0, state.PendingMessageCount);
    }

    [Fact]
    public void EmptyStateStartsWithZeroPendingMessageCount()
    {
        var state = Empty();
        Assert.Equal(0, state.PendingMessageCount);
    }

    [Fact]
    public void SetToolOutput_TogglesFlag()
    {
        var state = Empty();

        Assert.False(state.ShowToolOutput);
        Assert.True(state.SetToolOutput(true).ShowToolOutput);
        Assert.False(state.SetToolOutput(true).SetToolOutput(false).ShowToolOutput);
    }

    [Fact]
    public void SetEditorComponent_UpsertsEditorSlot()
    {
        var state = Empty();
        var component = new ExtensionWidgetState("text", "editor content", "Ext", Placement: "editor");

        state = state.SetEditorComponent("ext-a", component);

        var slot = Assert.Single(state.BridgeSlots);
        Assert.Equal("editor:ext-a", slot.Id);
        Assert.Equal("editor", slot.Placement);
        Assert.Equal("ext-a", slot.SourceId);
        Assert.Equal("editor content", slot.Content);
    }

    [Fact]
    public void SetEditorComponent_ReplacesExistingSlotForSameExtension()
    {
        var state = Empty()
            .SetEditorComponent("ext-a", new ExtensionWidgetState("text", "first", "Ext", Placement: "editor"));

        state = state.SetEditorComponent("ext-a", new ExtensionWidgetState("text", "second", "Ext", Placement: "editor"));

        var slot = Assert.Single(state.BridgeSlots);
        Assert.Equal("second", slot.Content);
    }

    [Fact]
    public void SetEditorComponent_NullRemovesEditorSlot()
    {
        var state = Empty().SetEditorComponent("ext-a", new ExtensionWidgetState("text", "content", "Ext", Placement: "editor"));

        state = state.SetEditorComponent("ext-a", null);

        Assert.Empty(state.BridgeSlots);
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
}
