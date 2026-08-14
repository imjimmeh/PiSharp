using System.Runtime.CompilerServices;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.OpenAI;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Copilot;

/// <summary>
/// GitHub Copilot chat-completions provider. Copilot is the one non-OpenAI-compatible
/// endpoint on the provider breadth list: it requires spoofed editor headers, an OAuth
/// copilot token (never an API key), and serves /chat/completions on the base URL
/// without a /v1 prefix. See docs/pisharp-providers.md for the routing recipe.
/// </summary>
public sealed class GitHubCopilotProvider : HttpModelProvider
{
    /// <summary>Registered api name used by the generated catalog for github-copilot models.</summary>
    public const string ApiName = "github-copilot-chat";

    public GitHubCopilotProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
        : base(ApiName, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, requireAuthentication: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!credentials.IsAuthenticated)
        {
            await foreach (var evt in ErrorAfterStart(model, "No GitHub Copilot token found — run '/login github-copilot' first.", Logger, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
            yield break;
        }

        var payload = await InvokePayloadHookAsync(OpenAICompletionsRequestMapper.BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        using var request = CreateJsonRequest(HttpMethod.Post, ChatCompletionsUrl(model), payload, credentials);
        foreach (var (key, value) in CopilotConstants.Headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);

        await foreach (var evt in OpenAICompletionsStreamParser.ParseAsync(model, response, cancellationToken).ConfigureAwait(false)) yield return evt;
    }

    private static Uri ChatCompletionsUrl(ModelDescriptor model)
    {
        var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl)
            ? CopilotConstants.DefaultBaseUrl
            : model.BaseUrl.TrimEnd('/');

        // Copilot serves /chat/completions on the base URL itself (no /v1 prefix).
        // A full endpoint path in models.json (individual or enterprise) is honored verbatim.
        return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(baseUrl)
            : new Uri($"{baseUrl}/chat/completions");
    }
}
