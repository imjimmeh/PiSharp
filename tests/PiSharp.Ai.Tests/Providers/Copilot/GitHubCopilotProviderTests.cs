using System.Net;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Copilot;
using PiSharp.Ai.Providers.Shared;
using PiSharp.Ai.Registry;
using PiSharp.Ai.Providers;
using Xunit;

namespace PiSharp.Ai.Tests.Providers.Copilot;

public sealed class GitHubCopilotProviderTests
{
    private static readonly AgentContext Context = new("system", [], []);
    private static readonly AgentStreamOptions Options = new();

    private static GitHubCopilotProvider Provider(CapturingHandler handler, IOAuthStorage? storage = null)
        => new(new HttpClient(handler), new ProviderCredentialResolver(oauthStorage: storage));

    private static GitHubCopilotProvider ProviderWithToken(CapturingHandler handler, string token = "copilot-token")
    {
        var storage = new InMemoryOAuthStorage();
        storage.SetTokenAsync("github-copilot", token).GetAwaiter().GetResult();
        return Provider(handler, storage);
    }

    private static async Task<List<AssistantMessageEvent>> CollectAsync(GitHubCopilotProvider provider, ModelDescriptor model)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in provider.StreamAsync(model, Context, Options)) events.Add(evt);
        return events;
    }

    private static ModelDescriptor CopilotModel(string baseUrl = "") => new("github-copilot", "claude-sonnet-4.5", GitHubCopilotProvider.ApiName, BaseUrl: baseUrl, MaxTokens: 100);

    [Fact]
    public async Task UsesDefaultIndividualBaseUrlWithChatCompletionsRoute()
    {
        var handler = new CapturingHandler(ChatSse());
        var provider = ProviderWithToken(handler);

        await CollectAsync(provider, CopilotModel());

        Assert.Equal("https://api.individual.githubcopilot.com", handler.Request!.RequestUri!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/chat/completions", handler.Request.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task EnterpriseBaseUrlFromModelGetsChatCompletionsRoute()
    {
        var handler = new CapturingHandler(ChatSse());
        var provider = ProviderWithToken(handler);

        await CollectAsync(provider, CopilotModel("https://copilot-api.company.ghe.com"));

        Assert.Equal("https://copilot-api.company.ghe.com", handler.Request!.RequestUri!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/chat/completions", handler.Request.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task FullEndpointPathInModelBaseUrlIsUsedVerbatim()
    {
        var handler = new CapturingHandler(ChatSse());
        var provider = ProviderWithToken(handler);

        await CollectAsync(provider, CopilotModel("https://copilot-api.company.ghe.com/chat/completions"));

        Assert.Equal("https://copilot-api.company.ghe.com/chat/completions", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task RequestCarriesCopilotEditorHeadersAndOAuthBearerToken()
    {
        var handler = new CapturingHandler(ChatSse());
        var provider = ProviderWithToken(handler);

        await CollectAsync(provider, CopilotModel());

        Assert.Equal("GitHubCopilotChat/0.35.0", handler.Request!.Headers.GetValues("User-Agent").Single());
        Assert.Equal("vscode/1.107.0", handler.Request.Headers.GetValues("Editor-Version").Single());
        Assert.Equal("copilot-chat/0.35.0", handler.Request.Headers.GetValues("Editor-Plugin-Version").Single());
        Assert.Equal("vscode-chat", handler.Request.Headers.GetValues("Copilot-Integration-Id").Single());
        Assert.Equal("Bearer copilot-token", handler.Request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task PayloadIsOpenAiChatCompletionsShape()
    {
        var handler = new CapturingHandler(ChatSse());
        var provider = ProviderWithToken(handler);

        await CollectAsync(provider, CopilotModel());

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("claude-sonnet-4.5", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task MissingTokenEmitsSingleErrorEventWithLoginHint()
    {
        var handler = new CapturingHandler(ChatSse());
        var provider = Provider(handler);

        var events = await CollectAsync(provider, CopilotModel());

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events, evt => evt is AssistantMessageEvent.Error));
        Assert.Contains("/login github-copilot", error.ErrorMessage.ErrorMessage);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task StreamsCopilotSseBodyToTextAndTerminalEvents()
    {
        var handler = new CapturingHandler(ChatSse());
        var provider = ProviderWithToken(handler);

        var events = await CollectAsync(provider, CopilotModel());

        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextStart);
        Assert.Contains(events, evt => evt is AssistantMessageEvent.TextDelta delta && delta.Delta == "hello");
        Assert.IsType<AssistantMessageEvent.Done>(events.Last());
    }

    [Fact]
    public async Task NonSuccessResponseEmitsTerminalErrorAfterStart()
    {
        var handler = new CapturingHandler("bad", HttpStatusCode.Unauthorized);
        var provider = ProviderWithToken(handler);

        var events = await CollectAsync(provider, CopilotModel());

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        Assert.IsType<AssistantMessageEvent.Error>(events.Last());
    }

    [Fact]
    public async Task RegistryDispatchesGithubCopilotModelsToCopilotProvider()
    {
        try
        {
            ApiRegistry.Clear();
            BuiltInProviders.RegisterAll();

            var registration = ApiRegistry.Get(GitHubCopilotProvider.ApiName);
            Assert.NotNull(registration);
            Assert.Equal(BuiltInProviders.SourceId, registration!.SourceId);

            var events = new List<AssistantMessageEvent>();
            await foreach (var evt in ApiRegistry.StreamAsync(CopilotModel(), Context, Options)) events.Add(evt);

            // Reached the copilot provider (no token stored): login hint, not a raw 401.
            var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events, evt => evt is AssistantMessageEvent.Error));
            Assert.Contains("/login github-copilot", error.ErrorMessage.ErrorMessage);

            Assert.Throws<InvalidOperationException>(() => registration.CompleteAsync(
                new ModelDescriptor("github-copilot", "claude-sonnet-4.5", "openai-completions"),
                Context,
                Options).GetAwaiter().GetResult());
        }
        finally
        {
            ApiRegistry.Clear();
        }
    }

    private static string ChatSse()
        => "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
           "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
}
