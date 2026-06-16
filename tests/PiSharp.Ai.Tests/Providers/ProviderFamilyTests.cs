using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Bedrock;
using PiSharp.Ai.Providers.Google;
using PiSharp.Ai.Providers.Mistral;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class ProviderFamilyTests
{
    private static readonly AgentContext Context = new("system", [], []);
    private static readonly AgentStreamOptions Options = new(ApiKey: "key", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer key" });

    [Fact]
    public async Task GoogleProviderMapsGeminiTextUsageApiKeyQueryAndTerminalEvents()
    {
        var handler = new CapturingHandler("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"gem\"}]}}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":2,\"totalTokenCount\":3}}\n\n");
        var provider = new GoogleProvider(new HttpClient(handler), Credentials(apiKey: "google-key"));

        var events = await CollectAsync(provider, Model("google", GoogleProvider.ApiName, "https://google.test"));

        Assert.Contains("key=google-key", handler.Request!.RequestUri!.Query);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "gem");
        var done = Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.Equal(3, done.Message.Usage!.TotalTokens);
    }

    [Fact]
    public async Task GoogleProviderMapsFunctionCallsAndFallsBackUsageTotal()
    {
        var handler = new CapturingHandler("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"before\"},{\"functionCall\":{\"name\":\"lookup\",\"args\":{\"q\":1}}}]}}],\"usageMetadata\":{\"promptTokenCount\":4,\"candidatesTokenCount\":5}}\n\n");
        var provider = new GoogleProvider(new HttpClient(handler), Credentials(apiKey: "google-key"));

        var events = await CollectAsync(provider, Model("google", GoogleProvider.ApiName, "https://google.test"));

        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "before");
        var tool = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events.First(evt => evt is AssistantMessageEvent.ToolCallEnd));
        Assert.Equal("lookup", tool.ToolCall.Name);
        Assert.Equal(1, tool.ToolCall.Arguments.GetProperty("q").GetInt32());
        var done = Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.Equal(9, done.Message.Usage!.TotalTokens);
    }

    [Fact]
    public async Task VertexProviderUsesAmbientHeaderAuthAndDoesNotAddApiKeyQueryStrings()
    {
        var handler = new CapturingHandler("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"vertex\"}]}}]}\n\n");
        var provider = new GoogleVertexProvider(new HttpClient(handler), Credentials(headers: new Dictionary<string, string> { ["Authorization"] = "Bearer ambient" }));

        var events = await CollectAsync(provider, Model("google-vertex", GoogleVertexProvider.ApiName, "https://vertex.test"), new AgentStreamOptions(Metadata: new Dictionary<string, object?> { ["project"] = "p", ["location"] = "l" }));

        Assert.DoesNotContain("key=", handler.Request!.RequestUri!.Query);
        Assert.True(handler.Request.Headers.Contains("Authorization"));
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "vertex");
    }

    [Fact]
    public async Task BedrockProviderMapsConverseStreamTextDeltasAndAmbientAuthRequestPath()
    {
        var handler = new CapturingHandler("data: {\"contentBlockDelta\":{\"delta\":{\"text\":\"bed\"}}}\n\n");
        var provider = new BedrockProvider(new HttpClient(handler), Credentials(headers: new Dictionary<string, string> { ["Authorization"] = "AWS4-HMAC-SHA256 signed" }));

        var events = await CollectAsync(provider, Model("amazon-bedrock", BedrockProvider.ApiName, "https://bedrock.test"));

        Assert.Contains("/model/family-model/converse-stream", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "bed");
    }

    [Fact]
    public async Task MistralProviderMapsChatCompatibleDeltasAndAuthorizationHeader()
    {
        var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"mis\"}}]}\n\n" +
                                           "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        var provider = new MistralProvider(new HttpClient(handler), Credentials(apiKey: "mistral-key", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer mistral-key" }));

        var events = await CollectAsync(provider, Model("mistral", MistralProvider.ApiName, "https://mistral.test"));

        Assert.True(handler.Request!.Headers.Contains("Authorization"));
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "mis");
        Assert.IsType<AssistantMessageEvent.Done>(events.Last());
    }

    private static ModelDescriptor Model(string provider, string api, string baseUrl) => new(provider, "family-model", api, BaseUrl: baseUrl, MaxTokens: 100, Input: ["text"]);
    private static StaticCredentialResolver Credentials(string? apiKey = null, IReadOnlyDictionary<string, string>? headers = null) => new(new ProviderCredentialResult(ApiKey: apiKey, Headers: headers, IsAuthenticated: apiKey is not null || headers?.Count > 0));
    private static async Task<List<AssistantMessageEvent>> CollectAsync(PiSharp.Ai.Providers.IModelProvider provider, ModelDescriptor model, AgentStreamOptions? options = null)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in provider.StreamAsync(model, Context, options ?? Options)) events.Add(evt);
        return events;
    }
}
