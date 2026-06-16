using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Ai.Http;
using PiSharp.Ai.Providers.Shared;
using Xunit;

namespace PiSharp.Ai.Tests.Shared;

public sealed class ProviderUtilityTests
{
    [Fact]
    public async Task SseParserReadsEventsCommentsMultilineDataAndEmptyStreams()
    {
        var text = ":comment\n" +
                   "event: message\n" +
                   "id: abc\n" +
                   "data: one\n" +
                   "data: two\n\n" +
                   "data: [DONE]\n\n";

        var events = new List<SseEvent>();
        await foreach (var evt in SseParser.ReadAsync(text)) events.Add(evt);

        Assert.Equal(2, events.Count);
        Assert.Equal("message", events[0].Event);
        Assert.Equal("abc", events[0].Id);
        Assert.Equal("one\ntwo", events[0].Data);
        Assert.Equal("[DONE]", events[1].Data);

        var empty = new List<SseEvent>();
        await foreach (var evt in SseParser.ReadAsync(string.Empty)) empty.Add(evt);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task JsonStreamReaderRepairsControlCharactersAndReturnsEmptyObjectOnInvalidInput()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"text\":\"hello\nworld\"}"));
        var repaired = await JsonStreamReader.ReadObjectAsync(stream);

        Assert.Equal("hello\nworld", repaired.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Object, JsonStreamReader.ParseObjectOrEmpty("not-json").ValueKind);
        Assert.Empty(JsonStreamReader.ParseObjectOrEmpty("not-json").EnumerateObject());
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("max_tokens", "max_tokens")]
    [InlineData("tool_calls", "tool_use")]
    [InlineData("weird", "weird")]
    public void StopReasonMapperMapsKnownReasonsAndPassesUnknownThrough(string input, string expected)
        => Assert.Equal(expected, StopReasonMapper.Map(input));

    [Fact]
    public void ToolTransformerNormalizesInvalidIdsAndPreservesShortValidIds()
    {
        Assert.Equal("call_123", ToolTransformer.NormalizeToolCallId("call_123"));

        var normalized = ToolTransformer.NormalizeToolCallId("not valid because spaces and long text not valid because spaces and long text");

        Assert.StartsWith("tc_", normalized);
        Assert.True(ToolTransformer.IsValidToolCallId(normalized));
    }

    [Fact]
    public void MessageTransformerDowngradesImagesFlattensThinkingNormalizesToolIdsAndSynthesizesResults()
    {
        using var args = JsonDocument.Parse("{\"value\":1}");
        var context = new AgentContext(
            "system prompt",
            [
                new UserMessage([new TextContent("hello"), new ImageContent("image/png", "base64")]),
                new AssistantMessage([
                    new ThinkingContent("thought", "sig"),
                    new ToolCallContent("bad id with spaces", "lookup", args.RootElement.Clone())
                ]),
                new AssistantMessage([new TextContent("skip")], ErrorMessage: "provider failed")
            ],
            []);

        var messages = MessageTransformer.ToProviderMessages(context, supportsImages: false, flattenThinking: true);

        Assert.Contains(messages, message => message.Role == "system");
        Assert.Contains(messages.SelectMany(message => message.Content), content => content.Type == "text" && content.Text == "[unsupported image: image/png]");
        Assert.Contains(messages.SelectMany(message => message.Content), content => content.Type == "text" && content.Text == "thought");
        Assert.Contains(messages.SelectMany(message => message.Content), content => content.Type == "tool_call" && content.Id!.StartsWith("tc_"));
        Assert.Contains(messages, message => message.Role == "tool" && message.Content.Single().Type == "tool_result");
        Assert.DoesNotContain(messages.SelectMany(message => message.Content), content => content.Text == "skip");
    }

    [Fact]
    public void MessageTransformerRejectsToolResultWithoutPriorAssistantToolCall()
    {
        var context = new AgentContext(
            "system prompt",
            [AgentMessages.ToolResult("call-orphan", "read", "orphan result")],
            []);

        var exception = Assert.Throws<InvalidOperationException>(() => MessageTransformer.ToProviderMessages(context));

        Assert.Contains("call-orphan", exception.Message, StringComparison.Ordinal);
    }
}
