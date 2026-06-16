using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Providers.Faux;
using PiSharp.Ai.Registry;
using Xunit;

namespace PiSharp.Ai.Tests;

public sealed class ProviderMatrixTests : IDisposable
{
    public ProviderMatrixTests() => ApiRegistry.Clear();
    public void Dispose() => ApiRegistry.Clear();

    [Fact]
    public async Task MatrixCoversEveryApiRegisteredByBuiltInProvidersApiNames()
    {
        foreach (var api in global::PiSharp.Ai.Providers.BuiltInProviders.ApiNames)
        {
            ApiRegistry.Register(new FauxProvider([FauxResponseItem.Text(api)], api), global::PiSharp.Ai.Providers.BuiltInProviders.SourceId);
        }

        Assert.Equal(global::PiSharp.Ai.Providers.BuiltInProviders.ApiNames.OrderBy(x => x), ApiRegistry.List().Select(r => r.Api).OrderBy(x => x));

        foreach (var api in global::PiSharp.Ai.Providers.BuiltInProviders.ApiNames)
        {
            var model = new ModelDescriptor("matrix", $"model-{api}", api);
            var events = new List<AssistantMessageEvent>();
            await foreach (var evt in ApiRegistry.StreamAsync(model, new AgentContext("system", [], []), new AgentStreamOptions())) events.Add(evt);

            Assert.Single(events, evt => evt is AssistantMessageEvent.Start);
            Assert.Single(events, evt => evt is AssistantMessageEvent.Done or AssistantMessageEvent.Error);
            AssertTextOrdering(events);
        }
    }

    private static void AssertTextOrdering(IReadOnlyList<AssistantMessageEvent> events)
    {
        var textStart = events.Select((evt, index) => (evt, index)).FirstOrDefault(pair => pair.evt is AssistantMessageEvent.TextStart).index;
        var textEnd = events.Select((evt, index) => (evt, index)).FirstOrDefault(pair => pair.evt is AssistantMessageEvent.TextEnd).index;
        foreach (var (_, index) in events.Select((evt, index) => (evt, index)).Where(pair => pair.evt is AssistantMessageEvent.TextDelta))
        {
            Assert.True(index > textStart);
            Assert.True(index < textEnd);
        }
    }
}
