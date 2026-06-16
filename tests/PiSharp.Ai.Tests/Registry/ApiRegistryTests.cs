using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Faux;
using PiSharp.Ai.Registry;
using Xunit;

namespace PiSharp.Ai.Tests.Registry;

public sealed class ApiRegistryTests : IDisposable
{
    public ApiRegistryTests() => ApiRegistry.Clear();

    public void Dispose() => ApiRegistry.Clear();

    [Fact]
    public void RegisterStoresProviderByApi()
    {
        var provider = new FauxProvider(api: "test-api");

        var registration = ApiRegistry.Register(provider, "test-source");

        Assert.Equal("test-api", registration.Api);
        Assert.Same(provider, ApiRegistry.Get("test-api")?.Provider);
        Assert.Equal("test-source", ApiRegistry.List().Single().SourceId);
    }

    [Fact]
    public void ResolveThrowsForMissingApi()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ApiRegistry.Resolve("missing-api"));
        Assert.Contains("missing-api", error.Message);
    }

    [Fact]
    public async Task StreamAndCompleteDispatchThroughFauxProvider()
    {
        var provider = new FauxProvider([FauxResponseItem.Text("ok")], "expected-api");
        ApiRegistry.Register(provider);
        var model = new ModelDescriptor("test", "test/model", "expected-api");

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in ApiRegistry.StreamAsync(model, new AgentContext("system", [], []), new AgentStreamOptions()))
        {
            events.Add(evt);
        }
        var message = await ApiRegistry.CompleteAsync(model, new AgentContext("system", [], []), new AgentStreamOptions());

        Assert.True(provider.WasStreamCalled);
        Assert.True(provider.WasCompleteCalled);
        Assert.IsType<AssistantMessageEvent.Start>(events.First());
        Assert.IsType<AssistantMessageEvent.Done>(events.Last());
        Assert.Contains(message.Content, content => content is TextContent { Text: "ok" });
    }

    [Fact]
    public void StreamRejectsMismatchedApi()
    {
        var provider = new FauxProvider(api: "expected-api");
        var registration = ApiRegistry.Register(provider);
        var model = new ModelDescriptor("test", "test/model", "wrong-api");

        var error = Assert.Throws<InvalidOperationException>(() =>
            registration.StreamAsync(model, new AgentContext("system", [], []), new AgentStreamOptions()).GetAsyncEnumerator().MoveNextAsync().AsTask().GetAwaiter().GetResult());

        Assert.Contains("Mismatched api", error.Message);
    }

    [Fact]
    public void UnregisterBySourceRemovesOnlyMatchingRegistrations()
    {
        ApiRegistry.Register(new FauxProvider(api: "api-a"), "source-a");
        ApiRegistry.Register(new FauxProvider(api: "api-b"), "source-b");

        var removed = ApiRegistry.UnregisterBySource("source-a");

        Assert.Equal(1, removed);
        Assert.Null(ApiRegistry.Get("api-a"));
        Assert.NotNull(ApiRegistry.Get("api-b"));
    }

    [Fact]
    public void ClearRemovesAllProviders()
    {
        ApiRegistry.Register(new FauxProvider(api: "api-a"));
        ApiRegistry.Register(new FauxProvider(api: "api-b"));

        ApiRegistry.Clear();

        Assert.Empty(ApiRegistry.List());
    }
}
