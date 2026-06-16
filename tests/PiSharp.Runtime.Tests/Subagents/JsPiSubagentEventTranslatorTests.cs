using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Serialization;
using PiSharp.Runtime.Subagents;
using Xunit;

namespace PiSharp.Runtime.Tests.Subagents;

public sealed class JsPiSubagentEventTranslatorTests
{
    [Fact]
    public void TranslatesTurnStartToJsPiTurnStart()
    {
        var translated = JsPiSubagentEventTranslator.Translate(new AgentEvent.TurnStart());

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"turn_start\"", json);
    }

    [Fact]
    public void TranslatesAssistantMessageEndWithTextContent()
    {
        var message = new AssistantMessage(
            [new TextContent("summary")],
            Api: "faux",
            Provider: "faux",
            Model: "faux-model",
            StopReason: "stop",
            Usage: new UsageInfo(Input: 1, Output: 2, TotalTokens: 3));

        var translated = JsPiSubagentEventTranslator.MessageEnd(message).Single();
        var json = AgentJsonSerializer.Serialize(translated);

        Assert.Contains("\"type\":\"message_end\"", json);
        Assert.Contains("\"role\":\"assistant\"", json);
        Assert.Contains("\"text\":\"summary\"", json);
        Assert.Contains("\"stopReason\":\"stop\"", json);
    }

    [Fact]
    public void TranslateAgentStartDoesNotThrow()
    {
        var translated = JsPiSubagentEventTranslator.Translate(new AgentEvent.AgentStart());

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"agent_start\"", json);
    }

    [Fact]
    public void TranslateNullEventThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            JsPiSubagentEventTranslator.Translate(null!));
    }

    [Fact]
    public void MessageEndNullMessageThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            JsPiSubagentEventTranslator.MessageEnd(null!));
    }

    [Fact]
    public void TranslateAgentEndContainsTypeAndMessages()
    {
        var messages = new AgentMessage[] { AgentMessages.User("hello"), AgentMessages.Assistant("world") };
        var translated = JsPiSubagentEventTranslator.Translate(new AgentEvent.AgentEnd(messages));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"agent_end\"", json);
        Assert.Contains("\"hello\"", json);
        Assert.Contains("\"world\"", json);
    }

    [Fact]
    public void TranslateTurnEndContainsTypeMessageAndToolResults()
    {
        var message = AgentMessages.Assistant("done");
        var translated = JsPiSubagentEventTranslator.Translate(new AgentEvent.TurnEnd(message, []));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"turn_end\"", json);
        Assert.Contains("\"done\"", json);
    }

    [Fact]
    public void TranslateMessageStartContainsTypeAndMessage()
    {
        var message = AgentMessages.User("hello");
        var translated = JsPiSubagentEventTranslator.Translate(new AgentEvent.MessageStart(message));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"message_start\"", json);
        Assert.Contains("\"hello\"", json);
    }

    [Fact]
    public void TranslateMessageUpdateContainsTypeMessageAndAssistantEvent()
    {
        var message = AgentMessages.Assistant("streaming");
        var assistantEvent = new AssistantMessageEvent.TextDelta(
            new AssistantMessage([new TextContent("stream")], Api: "a", Provider: "p", Model: "m"),
            0,
            "ing");
        var translated = JsPiSubagentEventTranslator.Translate(new AgentEvent.MessageUpdate(message, assistantEvent));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"message_update\"", json);
        Assert.Contains("\"streaming\"", json);
    }

    [Fact]
    public void TranslateMessageEndCoreContainsTypeAndMessage()
    {
        var message = AgentMessages.Assistant("final");
        var translated = JsPiSubagentEventTranslator.Translate(new AgentEvent.MessageEnd(message));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"message_end\"", json);
        Assert.Contains("\"final\"", json);
    }

    [Fact]
    public void TranslateToolExecutionStartContainsTypeAndToolInfo()
    {
        using var doc = JsonDocument.Parse("{\"input\":\"value\"}");
        var translated = JsPiSubagentEventTranslator.Translate(
            new AgentEvent.ToolExecutionStart("call-1", "search", doc.RootElement.Clone()));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"tool_execution_start\"", json);
        Assert.Contains("\"call-1\"", json);
        Assert.Contains("\"search\"", json);
    }

    [Fact]
    public void TranslateToolExecutionStartUsesJsPiArgsPropertyName()
    {
        using var doc = JsonDocument.Parse("{\"input\":\"value\"}");
        var translated = JsPiSubagentEventTranslator.Translate(
            new AgentEvent.ToolExecutionStart("call-1", "search", doc.RootElement.Clone()));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"args\":{\"input\":\"value\"}", json);
        Assert.DoesNotContain("\"arguments\"", json);
    }

    [Fact]
    public void TranslateToolExecutionUpdateUsesJsPiArgsPropertyName()
    {
        using var doc = JsonDocument.Parse("{\"input\":\"value\"}");
        var translated = JsPiSubagentEventTranslator.Translate(
            new AgentEvent.ToolExecutionUpdate("call-1", "search", doc.RootElement.Clone(), "partial"));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"args\":{\"input\":\"value\"}", json);
        Assert.DoesNotContain("\"arguments\"", json);
    }

    [Fact]
    public void TranslateToolExecutionEndContainsTypeAndResult()
    {
        var translated = JsPiSubagentEventTranslator.Translate(
            new AgentEvent.ToolExecutionEnd("call-1", "search", "found it", false));

        var json = AgentJsonSerializer.Serialize(translated.Single());
        Assert.Contains("\"type\":\"tool_execution_end\"", json);
        Assert.Contains("\"found it\"", json);
    }
}
