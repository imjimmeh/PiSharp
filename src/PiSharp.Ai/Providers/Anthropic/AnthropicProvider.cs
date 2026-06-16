using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Anthropic;

public sealed class AnthropicProvider : HttpModelProvider
{
    public const string ApiName = "anthropic-messages";

    public AnthropicProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
        : base(ApiName, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = await InvokePayloadHookAsync(AnthropicRequestMapper.BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        using var request = CreateJsonRequest(HttpMethod.Post, new Uri($"{BaseUrl(model)}/v1/messages"), payload, credentials);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!string.IsNullOrWhiteSpace(credentials.ApiKey)) request.Headers.TryAddWithoutValidation("x-api-key", credentials.ApiKey);

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);

        await foreach (var evt in AnthropicStreamParser.ParseAsync(model, response, cancellationToken).ConfigureAwait(false)) yield return evt;
    }

    private static System.Text.Json.JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
        => AnthropicRequestMapper.BuildPayload(model, context, options);

    private static string BaseUrl(ModelDescriptor model) => string.IsNullOrWhiteSpace(model.BaseUrl) ? "https://api.anthropic.com" : model.BaseUrl.TrimEnd('/');
}

internal static class JsonElementExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)) return value;
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
