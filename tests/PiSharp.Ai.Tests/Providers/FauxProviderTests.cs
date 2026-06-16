using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Faux;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class FauxProviderTests
{
    private static readonly ModelDescriptor Model = new("faux-provider", "faux-model", FauxProvider.DefaultApi);
    private static readonly AgentContext Context = new("system", [], []);
    private static readonly AgentStreamOptions Options = new();

    [Fact]
    public async Task StreamEmitsStartBeforeUpdatesAndExactlyOneTerminalEvent()
    {
        var provider = new FauxProvider([FauxResponseItem.Text("hello")]);

        var events = await CollectAsync(provider);

        Assert.IsType<AssistantMessageEvent.Start>(events.First());
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextStart);
        Assert.Single(events, evt => evt is AssistantMessageEvent.Done or AssistantMessageEvent.Error);
        Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.True(provider.WasStreamCalled);
    }

    [Fact]
    public async Task StreamSupportsThinkingToolCallErrorAndAbortItems()
    {
        using var args = JsonDocument.Parse("{\"query\":\"value\"}");
        var provider = new FauxProvider([
            FauxResponseItem.Thinking("considering"),
            FauxResponseItem.ToolCall("bad id", "lookup", args.RootElement)
        ]);

        var events = await CollectAsync(provider);

        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ToolCallEnd toolEnd && toolEnd.ToolCall.Id.StartsWith("tc_"));

        var errorProvider = new FauxProvider([FauxResponseItem.Error("boom")]);
        var errorEvents = await CollectAsync(errorProvider);
        Assert.IsType<AssistantMessageEvent.Error>(errorEvents.Last());

        var abortProvider = new FauxProvider([FauxResponseItem.Abort()]);
        var abortEvents = await CollectAsync(abortProvider);
        Assert.IsType<AssistantMessageEvent.Error>(abortEvents.Last());
    }

    [Fact]
    public async Task CompleteAsyncReturnsFinalAssistantMessageWithUsage()
    {
        var provider = new FauxProvider([FauxResponseItem.Text("hello")]);

        var message = await provider.CompleteAsync(Model, Context, Options);

        Assert.True(provider.WasCompleteCalled);
        Assert.Equal("stop", message.StopReason);
        Assert.NotNull(message.Usage);
        Assert.Equal(5, message.Usage!.Output);
        Assert.Contains(message.Content, content => content is TextContent { Text: "hello" });
    }

    private static async Task<List<AssistantMessageEvent>> CollectAsync(FauxProvider provider)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in provider.StreamAsync(Model, Context, Options)) events.Add(evt);
        return events;
    }
}
