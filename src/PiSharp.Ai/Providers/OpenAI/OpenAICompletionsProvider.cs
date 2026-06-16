using System.Runtime.CompilerServices;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.OpenAI;

public sealed class OpenAICompletionsProvider : HttpModelProvider
{
    public const string ApiName = "openai-completions";

    public OpenAICompletionsProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null, string api = ApiName)
        : base(api, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = await InvokePayloadHookAsync(OpenAICompletionsRequestMapper.BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        using var request = CreateJsonRequest(HttpMethod.Post, OpenAIEndpoint.Url(model, "chat/completions"), payload, credentials);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);

        await foreach (var evt in OpenAICompletionsStreamParser.ParseAsync(model, response, cancellationToken).ConfigureAwait(false)) yield return evt;
    }

    private static System.Text.Json.JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
        => OpenAICompletionsRequestMapper.BuildPayload(model, context, options);
}
