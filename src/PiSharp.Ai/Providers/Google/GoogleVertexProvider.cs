using System.Runtime.CompilerServices;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;

namespace PiSharp.Ai.Providers.Google;

public sealed class GoogleVertexProvider : GoogleProvider
{
    public new const string ApiName = "google-vertex";

    public GoogleVertexProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
        : base(httpClient, credentialResolver, ApiName)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = await InvokePayloadHookAsync(GoogleRequestMapper.BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        var project = options.Metadata is not null && options.Metadata.TryGetValue("project", out var p) ? p?.ToString() : "test-project";
        var location = options.Metadata is not null && options.Metadata.TryGetValue("location", out var l) ? l?.ToString() : "us-central1";
        var url = $"{BaseUrl(model)}/v1/projects/{Uri.EscapeDataString(project ?? "test-project")}/locations/{Uri.EscapeDataString(location ?? "us-central1")}/publishers/google/models/{Uri.EscapeDataString(model.Id)}:streamGenerateContent?alt=sse";
        using var request = CreateJsonRequest(HttpMethod.Post, new Uri(url), payload, credentials);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);
        await foreach (var evt in GoogleStreamParser.ParseAsync(model, response, cancellationToken).ConfigureAwait(false)) yield return evt;
    }

    protected override string BaseUrl(ModelDescriptor model) => string.IsNullOrWhiteSpace(model.BaseUrl) ? "https://aiplatform.googleapis.com" : model.BaseUrl.TrimEnd('/');
}
