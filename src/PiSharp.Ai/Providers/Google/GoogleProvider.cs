using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Google;

public class GoogleProvider : HttpModelProvider
{
    public const string ApiName = "google-generative-ai";

    public GoogleProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null, string api = ApiName)
        : base(api, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = await InvokePayloadHookAsync(GoogleRequestMapper.BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        var url = $"{BaseUrl(model)}/v1beta/models/{Uri.EscapeDataString(model.Id)}:streamGenerateContent?alt=sse";
        if (!string.IsNullOrWhiteSpace(credentials.ApiKey)) url += $"&key={Uri.EscapeDataString(credentials.ApiKey)}";
        using var request = CreateJsonRequest(HttpMethod.Post, new Uri(url), payload, credentials);
        request.Headers.Remove("Authorization");
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);
        await foreach (var evt in GoogleStreamParser.ParseAsync(model, response, cancellationToken).ConfigureAwait(false)) yield return evt;
    }

    protected static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
        => GoogleRequestMapper.BuildPayload(model, context, options);

    protected virtual string BaseUrl(ModelDescriptor model) => string.IsNullOrWhiteSpace(model.BaseUrl) ? "https://generativelanguage.googleapis.com" : model.BaseUrl.TrimEnd('/');
}

internal static class GoogleJsonExtensions
{
    public static JsonElement GetObj(this JsonElement e, string n)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)) return v;
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public static string? GetPathString(this JsonElement e, params object[] path)
    {
        var current = e;
        foreach (var segment in path)
        {
            if (segment is string name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
            }
            else if (segment is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index) return null;
                current = current[index];
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
