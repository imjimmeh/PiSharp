using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class DefaultProviderTimeoutTests
{
    [Fact]
    public void Provider_creates_default_http_client_with_ten_minute_timeout()
    {
        var provider = new ProbeProvider();

        Assert.Equal(TimeSpan.FromMinutes(10), provider.Client.Timeout);
    }

    private sealed class ProbeProvider : HttpModelProvider
    {
        public ProbeProvider() : base("probe")
        {
        }

        public HttpClient Client => HttpClient;

        public override IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
            ModelDescriptor model,
            AgentContext context,
            AgentStreamOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}