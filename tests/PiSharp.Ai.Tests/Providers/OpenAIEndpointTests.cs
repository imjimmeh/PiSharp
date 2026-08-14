using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.OpenAI;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class OpenAIEndpointTests
{
    private static readonly AgentContext Context = new("system", [], []);
    private static readonly AgentStreamOptions Options = new();
    private const string DoneSse = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";

    private static OpenAICompletionsProvider Provider(CapturingHandler handler)
        => new(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));

    private static async Task<string> RequestPathAsync(string api, string baseUrl)
    {
        var handler = new CapturingHandler(DoneSse);
        var provider = Provider(handler);
        var model = new ModelDescriptor("p", "m", api, BaseUrl: baseUrl, MaxTokens: 100);

        await foreach (var _ in provider.StreamAsync(model, Context, Options)) { }

        return handler.Request!.RequestUri!.AbsolutePath;
    }

    [Theory]
    [InlineData("https://openai.test", "/v1/chat/completions")]
    [InlineData("https://openai.test/v1", "/v1/chat/completions")]
    [InlineData("https://api.groq.com/openai/v1", "/openai/v1/chat/completions")]
    [InlineData("https://api.cerebras.ai/v1", "/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "/v1/chat/completions")]
    [InlineData("https://router.huggingface.co/v1", "/v1/chat/completions")]
    [InlineData("https://api.opencode.ai/v1", "/v1/chat/completions")]
    [InlineData("https://api.cloudflare.com/client/v4/accounts/acct/ai/v1", "/client/v4/accounts/acct/ai/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1", "/api/v1/chat/completions")]
    public async Task StandardBasesAppendChatCompletionsWithoutDuplicatingV1(string baseUrl, string expectedPath)
    {
        Assert.Equal(expectedPath, await RequestPathAsync(OpenAICompletionsProvider.ApiName, baseUrl));
    }

    [Theory]
    [InlineData("https://api.z.ai/api/paas/v4", "/api/paas/v4/chat/completions")]
    [InlineData("https://gateway.ai.cloudflare.com/v1/acct/gw/compat", "/v1/acct/gw/compat/chat/completions")]
    [InlineData("https://token-plan-cn.xiaomimimo.com/v1", "/v1/chat/completions")]
    public async Task EmbeddedVersionBasesDoNotGetSpuriousV1Segment(string baseUrl, string expectedPath)
    {
        Assert.Equal(expectedPath, await RequestPathAsync(OpenAICompletionsProvider.ApiName, baseUrl));
    }

    [Fact]
    public async Task FullEndpointPathInBaseUrlIsUsedVerbatim()
    {
        Assert.Equal("/api/paas/v4/chat/completions", await RequestPathAsync(OpenAICompletionsProvider.ApiName, "https://api.z.ai/api/paas/v4/chat/completions"));
    }

    [Fact]
    public async Task ResponsesApiStillBuildsResponsesRoute()
    {
        var handler = new CapturingHandler("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
        var provider = new OpenAIResponsesProvider(new HttpClient(handler), new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));
        var model = new ModelDescriptor("openai", "gpt-test", OpenAIResponsesProvider.ApiName, BaseUrl: "https://openai.test", MaxTokens: 100);

        await foreach (var _ in provider.StreamAsync(model, Context, Options)) { }

        Assert.Equal("/v1/responses", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task HostEndingInV4WordIsNotTreatedAsVersionSegment()
    {
        Assert.Equal("/v1/chat/completions", await RequestPathAsync(OpenAICompletionsProvider.ApiName, "https://api.v4host.example.com"));
    }
}
