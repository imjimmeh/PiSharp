using System.Net;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;

namespace PiSharp.Ai.Tests.Providers;

internal sealed class StaticCredentialResolver(ProviderCredentialResult result) : IProviderCredentialResolver
{
    public Task<ProviderCredentialResult> ResolveAsync(ModelDescriptor model, AgentStreamOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(result);
}

internal sealed class CapturingHandler(string response, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? RequestBody { get; private set; }
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Request = request;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(response)
        };
    }
}
