using System.Net;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Anthropic;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class AnthropicProviderTests
{
    private static readonly AgentContext Context = new("system", [AgentMessages.User("hello")], []);
    private static readonly ModelDescriptor Model = new(
        "anthropic", "claude-test", AnthropicProvider.ApiName,
        BaseUrl: "https://anthropic.test",
        MaxTokens: 4096,
        ThinkingLevelMap: new Dictionary<string, int> { ["high"] = 1024 });

    [Fact]
    public async Task TextStreamFixtureMapsToStartTextAndDone()
    {
        var provider = Provider("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":2}}}\n\n" +
                                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
                                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\n" +
                                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

        var events = await CollectAsync(provider);

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "hi");
        var done = Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.Equal("stop", done.Message.StopReason);
        Assert.Equal(3, done.Message.Usage!.Output);
    }

    [Fact]
    public async Task ToolCallStreamFixtureMapsInputJsonDeltasToToolCallContent()
    {
        var provider = Provider("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"tool 1\",\"name\":\"lookup\"}}\n\n" +
                                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"q\\\":\\\"x\\\"}\"}}\n\n" +
                                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\"}\n\n" +
                                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

        var events = await CollectAsync(provider);

        Assert.Contains(events, evt => evt is AssistantMessageEvent.ToolCallEnd tool && tool.ToolCall.Name == "lookup" && tool.ToolCall.Arguments.GetProperty("q").GetString() == "x");
    }

    [Fact]
    public async Task ThinkingDeltaEmitsThinkingEventsAndToolStopReasonIsNormalized()
    {
        var provider = Provider("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"thinking\",\"thinking\":\"ponder\"}}\n\n" +
                                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"deeper\"}}\n\n" +
                                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\"}\n\n" +
                                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n\n" +
                                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

        var events = await CollectAsync(provider);

        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingDelta delta && delta.Delta == "deeper");
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingEnd);
        var done = Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.Equal("tool_use", done.Message.StopReason);
    }

    [Fact]
    public async Task RequestIncludesAnthropicHeadersStreamMaxTokensAndThinkingBudget()
    {
        var handler = new CapturingHandler("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        var provider = Provider(handler);

        await CollectAsync(provider, new AgentStreamOptions(ApiKey: "key", Reasoning: "high", MaxTokens: 100));

        Assert.Equal("/v1/messages", handler.Request!.RequestUri!.AbsolutePath);
        Assert.True(handler.Request.Headers.Contains("anthropic-version"));
        Assert.True(handler.Request.Headers.Contains("x-api-key"));
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(100, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(1024, body.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    [Fact]
    public async Task CredentialLessResolverThrowsBeforeAnyLiveProviderCallIsAttempted()
    {
        var handler = new CapturingHandler("{}");
        var provider = new AnthropicProvider(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult()));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await CollectAsync(provider));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NonSuccessHttpResponseEmitsErrorAfterStart()
    {
        var handler = new CapturingHandler("bad", HttpStatusCode.BadRequest);
        var provider = Provider(handler);

        var events = await CollectAsync(provider);

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        Assert.IsType<AssistantMessageEvent.Error>(events.Last());
    }

    [Fact]
    public async Task CompleteAsyncReturnsFinalAssistantMessageFromTerminalEvent()
    {
        var provider = Provider("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"done\"}}\n\n" +
                                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

        var message = await provider.CompleteAsync(Model, Context, new AgentStreamOptions(ApiKey: "key"));

        Assert.Contains(message.Content, content => content is TextContent { Text: "done" });
    }

    private static AnthropicProvider Provider(string sse) => Provider(new CapturingHandler(sse));
    private static AnthropicProvider Provider(CapturingHandler handler) => new(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));
    private static async Task<List<AssistantMessageEvent>> CollectAsync(AnthropicProvider provider, AgentStreamOptions? options = null)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in provider.StreamAsync(Model, Context, options ?? new AgentStreamOptions(ApiKey: "key"))) events.Add(evt);
        return events;
    }
}
