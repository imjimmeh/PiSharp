using System.Net;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Bedrock;
using PiSharp.Ai.Providers.Google;
using PiSharp.Ai.Providers.Mistral;
using PiSharp.Ai.Providers.OpenAI;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class ProviderStreamTests
{
    private static readonly AgentContext Context = new("system", [AgentMessages.User("hello")], []);
    private static readonly AgentStreamOptions Options = new(ApiKey: "key", MaxTokens: 50);

    [Fact]
    public async Task GoogleParsesThoughtPartsAsThinkingEvents()
    {
        var provider = Google("data: {\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"ponder\"}]}}]}\n\n" +
                              "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"response\"}]}}]}\n\n");

        var events = await CollectAsync(provider, Model("google", GoogleProvider.ApiName));

        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingDelta delta && delta.Delta == "ponder");
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "response");
    }

    [Fact]
    public async Task OpenAICompletionsParsesReasoningContentAsThinkingEvents()
    {
        var provider = Completions("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"ponder\"}}]}\n\n" +
                                    "data: {\"choices\":[{\"delta\":{\"content\":\"response\"}}]}\n\n" +
                                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");

        var events = await CollectAsync(provider, Model("openai", OpenAICompletionsProvider.ApiName));

        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingDelta delta && delta.Delta == "ponder");
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "response");
    }

    [Fact]
    public async Task MistralParsesReasoningContentAsThinkingEvents()
    {
        var provider = Mistral("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"ponder\"}}]}\n\n" +
                               "data: {\"choices\":[{\"delta\":{\"content\":\"response\"}}]}\n\n" +
                               "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");

        var events = await CollectAsync(provider, Model("mistral", MistralProvider.ApiName));

        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingDelta delta && delta.Delta == "ponder");
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "response");
    }

    [Fact]
    public async Task BedrockParsesReasoningContentBlockAsThinkingEvents()
    {
        var provider = Bedrock("data: {\"contentBlockStart\":{\"start\":{\"reasoningContent\":{\"reasoningText\":\"ponder\"}}}}\n\n" +
                               "data: {\"contentBlockDelta\":{\"delta\":{\"text\":\"deeper\"}}}\n\n" +
                               "data: {\"contentBlockStop\":{}}\n\n" +
                               "data: {\"contentBlockStart\":{\"start\":{\"text\":\"response\"}}}\n\n" +
                               "data: {\"contentBlockDelta\":{\"delta\":{\"text\":\"text\"}}}\n\n");

        var events = await CollectAsync(provider, Model("amazon", BedrockProvider.ApiName));

        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.ThinkingDelta delta && delta.Delta == "deeper");
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "text");
    }

    private static ModelDescriptor Model(string provider, string api) => new(provider, "test-model", api, BaseUrl: "https://test.local", MaxTokens: 100);

    private static GoogleProvider Google(string sse) => Google(new CapturingHandler(sse));
    private static GoogleProvider Google(CapturingHandler handler) => new(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

    private static OpenAICompletionsProvider Completions(string sse) => Completions(new CapturingHandler(sse));
    private static OpenAICompletionsProvider Completions(CapturingHandler handler) => new(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

    private static MistralProvider Mistral(string sse) => Mistral(new CapturingHandler(sse));
    private static MistralProvider Mistral(CapturingHandler handler) => new(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

    private static BedrockProvider Bedrock(string sse) => Bedrock(new CapturingHandler(sse));
    private static BedrockProvider Bedrock(CapturingHandler handler) => new(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

    private static async Task<List<AssistantMessageEvent>> CollectAsync(PiSharp.Ai.Providers.IModelProvider provider, ModelDescriptor model)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in provider.StreamAsync(model, Context, Options)) events.Add(evt);
        return events;
    }
}
