using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Streaming;
using Xunit;

namespace PiSharp.Acp.Tests;

/// <summary>
/// Asserts the exact <c>session/update</c> payload shapes produced from
/// <see cref="AgentHarnessEvent"/> values (plan §5.5 / §10).
/// </summary>
public sealed class AcpEventTranslatorTests
{
    private static readonly AcpEventTranslator Translator = new("sess_test");

    private static JsonDocument Update(object update) => JsonDocument.Parse(JsonSerializer.Serialize(update, update.GetType()));

    private static AssistantMessage Msg(params MessageContent[] content)
        => new(content, StopReason: "end_turn");

    [Fact]
    public void MessageStart_AssignsMessageIdAndEmitsAgentMessageChunk()
    {
        var updates = Translator.Translate(
            new AgentHarnessEvent.Core(new AgentEvent.MessageStart(Msg(new TextContent("hello")))));
        var doc = Update(Assert.Single(updates));
        var root = doc.RootElement;
        Assert.Equal("agent_message_chunk", root.GetProperty("sessionUpdate").GetString());
        Assert.Equal("msg_0", root.GetProperty("messageId").GetString());
        Assert.Equal("hello", root.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public void MessageStart_WithThinking_EmitsThoughtChunk()
    {
        var updates = Translator.Translate(
            new AgentHarnessEvent.Core(new AgentEvent.MessageStart(Msg(new ThinkingContent("pondering")))));
        var doc = Update(Assert.Single(updates));
        Assert.Equal("agent_thought_chunk", doc.RootElement.GetProperty("sessionUpdate").GetString());
        Assert.Equal("pondering", doc.RootElement.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public void MessageUpdate_TextDelta_ReusesActiveMessageId()
    {
        var startMsg = Msg(new TextContent("he"));
        var deltaMsg = Msg(new TextContent("hello"));
        Translator.Translate(new AgentHarnessEvent.Core(new AgentEvent.MessageStart(startMsg)));
        var updates = Translator.Translate(
            new AgentHarnessEvent.Core(new AgentEvent.MessageUpdate(deltaMsg, new AssistantMessageEvent.TextDelta(deltaMsg, 0, "llo"))));
        var doc = Update(Assert.Single(updates));
        Assert.Equal("msg_0", doc.RootElement.GetProperty("messageId").GetString());
        Assert.Equal("llo", doc.RootElement.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public void NewMessageStart_AssignsNextMessageId()
    {
        Translator.Translate(new AgentHarnessEvent.Core(new AgentEvent.MessageStart(Msg(new TextContent("a")))));
        var updates = Translator.Translate(new AgentHarnessEvent.Core(new AgentEvent.MessageStart(Msg(new TextContent("b")))));
        Assert.Equal("msg_1", Update(Assert.Single(updates)).RootElement.GetProperty("messageId").GetString());
    }

    [Fact]
    public void ToolCall_EmitsToolCallPendingUpdate()
    {
        var args = new Dictionary<string, object?> { ["path"] = "/x" };
        var updates = Translator.Translate(
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ToolCall("tc-1", "bash", args)));
        var root = Update(Assert.Single(updates)).RootElement;
        Assert.Equal("tool_call", root.GetProperty("sessionUpdate").GetString());
        Assert.Equal("tc-1", root.GetProperty("toolCallId").GetString());
        Assert.Equal("bash", root.GetProperty("title").GetString());
        Assert.Equal("execute", root.GetProperty("kind").GetString());
        Assert.Equal("pending", root.GetProperty("status").GetString());
    }

    [Fact]
    public void ToolExecutionStart_EmitsInProgressUpdate()
    {
        var updates = Translator.Translate(
            new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tc-1", "bash", JsonDocument.Parse("{}").RootElement.Clone())));
        var root = Update(Assert.Single(updates)).RootElement;
        Assert.Equal("tool_call_update", root.GetProperty("sessionUpdate").GetString());
        Assert.Equal("in_progress", root.GetProperty("status").GetString());
        Assert.Equal("tc-1", root.GetProperty("toolCallId").GetString());
    }

    [Fact]
    public void ToolExecutionEnd_Success_EmitsCompletedUpdateWithContent()
    {
        var updates = Translator.Translate(
            new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tc-1", "bash", "ok", IsError: false)));
        var root = Update(Assert.Single(updates)).RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        var content = Assert.Single(root.GetProperty("content").EnumerateArray());
        Assert.Equal("ok", content.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public void ToolExecutionEnd_Error_EmitsFailedUpdate()
    {
        var updates = Translator.Translate(
            new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionEnd("tc-1", "bash", "boom", IsError: true)));
        Assert.Equal("failed", Update(Assert.Single(updates)).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void TurnEnd_WithUsage_EmitsUsageUpdate()
    {
        var message = new AssistantMessage(
            [new TextContent("done")],
            Usage: new UsageInfo(Input: 10, Output: 5, Cost: new UsageCost(Input: 0.1m, Output: 0.2m)),
            StopReason: "end_turn");
        var updates = Translator.Translate(new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(message, [])));
        var root = Update(Assert.Single(updates)).RootElement;
        Assert.Equal("usage_update", root.GetProperty("sessionUpdate").GetString());
        Assert.Equal(15, root.GetProperty("used").GetInt32());
    }

    [Fact]
    public void TurnEnd_WithoutUsage_EmitsBareUsageUpdate()
    {
        var message = new AssistantMessage([new TextContent("done")]);
        var updates = Translator.Translate(new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(message, [])));
        var root = Update(Assert.Single(updates)).RootElement;
        Assert.Equal("usage_update", root.GetProperty("sessionUpdate").GetString());
        Assert.False(root.TryGetProperty("used", out _));
    }

    [Fact]
    public void SessionInfoChanged_EmitsSessionInfoUpdate()
    {
        var updates = Translator.Translate(
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionInfoChanged("my session")));
        var root = Update(Assert.Single(updates)).RootElement;
        Assert.Equal("session_info_update", root.GetProperty("sessionUpdate").GetString());
        Assert.Equal("my session", root.GetProperty("title").GetString());
    }

    [Fact]
    public void MessageEnd_EmitsNothing()
    {
        var updates = Translator.Translate(
            new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(Msg(new TextContent("x")))));
        Assert.Empty(updates);
    }

    [Theory]
    [InlineData("read", "read")]
    [InlineData("grep", "search")]
    [InlineData("find", "search")]
    [InlineData("ls", "search")]
    [InlineData("edit", "edit")]
    [InlineData("write", "edit")]
    [InlineData("bash", "execute")]
    [InlineData("custom-thing", "other")]
    public void MapToolKind_MapsKnownAndUnknown(string tool, string kind)
        => Assert.Equal(kind, AcpEventTranslator.MapToolKind(tool));

    [Fact]
    public void TranslateReplay_UserAssistantAndToolResult_Chunks()
    {
        var translator = new AcpEventTranslator("sess_r");
        var messages = new AgentMessage[]
        {
            new UserMessage([new TextContent("hi")]),
            new AssistantMessage([new TextContent("hello"), new ThinkingContent("hmm")], StopReason: "end_turn"),
            new ToolResultMessage("tc-1", "bash", [new TextContent("done")])
        };
        var updates = translator.TranslateReplay(messages).ToList();
        Assert.Equal(4, updates.Count);
        var u0 = Update(updates[0]).RootElement;
        Assert.Equal("user_message_chunk", u0.GetProperty("sessionUpdate").GetString());
        Assert.Equal("hi", u0.GetProperty("content").GetProperty("text").GetString());
        var u1 = Update(updates[1]).RootElement;
        Assert.Equal("agent_message_chunk", u1.GetProperty("sessionUpdate").GetString());
        Assert.Equal("hello", u1.GetProperty("content").GetProperty("text").GetString());
        var u2 = Update(updates[2]).RootElement;
        Assert.Equal("agent_thought_chunk", u2.GetProperty("sessionUpdate").GetString());
        var u3 = Update(updates[3]).RootElement;
        Assert.Equal("tool_call_update", u3.GetProperty("sessionUpdate").GetString());
        Assert.Equal("completed", u3.GetProperty("status").GetString());
        Assert.Equal("tc-1", u3.GetProperty("toolCallId").GetString());
    }
}
