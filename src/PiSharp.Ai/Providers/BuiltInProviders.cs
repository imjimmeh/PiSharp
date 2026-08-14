using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Anthropic;
using PiSharp.Ai.Providers.Bedrock;
using PiSharp.Ai.Providers.Copilot;
using PiSharp.Ai.Providers.Faux;
using PiSharp.Ai.Providers.Google;
using PiSharp.Ai.Providers.Mistral;
using PiSharp.Ai.Providers.OpenAI;
using PiSharp.Ai.Registry;

namespace PiSharp.Ai.Providers;

public static class BuiltInProviders
{
    public const string SourceId = "built-in";

    public static IReadOnlyList<string> ApiNames { get; } =
    [
        AnthropicProvider.ApiName,
        OpenAIResponsesProvider.ApiName,
        OpenAICompletionsProvider.ApiName,
        "openai-chat-completions",
        "azure-openai-responses",
        "openai-codex-responses",
        GoogleProvider.ApiName,
        GoogleVertexProvider.ApiName,
        BedrockProvider.ApiName,
        MistralProvider.ApiName,
        FauxProvider.DefaultApi,
        GitHubCopilotProvider.ApiName
    ];

    public static IReadOnlyList<RegisteredApiProvider> RegisterAll(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
    {
        var registrations = new List<RegisteredApiProvider>
        {
            ApiRegistry.Register(new AnthropicProvider(httpClient, credentialResolver), SourceId),
            ApiRegistry.Register(new OpenAIResponsesProvider(httpClient, credentialResolver), SourceId),
            ApiRegistry.Register(new OpenAICompletionsProvider(httpClient, credentialResolver), SourceId),
            ApiRegistry.Register(new OpenAICompletionsProvider(httpClient, credentialResolver, "openai-chat-completions"), SourceId),
            ApiRegistry.Register(new OpenAIResponsesProvider(httpClient, credentialResolver, "azure-openai-responses"), SourceId),
            ApiRegistry.Register(new OpenAIResponsesProvider(httpClient, credentialResolver, "openai-codex-responses"), SourceId),
            ApiRegistry.Register(new GoogleProvider(httpClient, credentialResolver), SourceId),
            ApiRegistry.Register(new GoogleVertexProvider(httpClient, credentialResolver), SourceId),
            ApiRegistry.Register(new BedrockProvider(httpClient, credentialResolver), SourceId),
            ApiRegistry.Register(new MistralProvider(httpClient, credentialResolver), SourceId),
            ApiRegistry.Register(new FauxProvider(), SourceId),
            ApiRegistry.Register(new GitHubCopilotProvider(httpClient, credentialResolver), SourceId)
        };

        return registrations;
    }

    public static int Clear()
        => ApiRegistry.UnregisterBySource(SourceId);
}
