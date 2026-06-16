using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Faux;
using PiSharp.Ai.Registry;
using Xunit;

namespace PiSharp.Ai.Tests.Contract;

public sealed class ProviderContractTests : IDisposable
{
    public ProviderContractTests() => ApiRegistry.Clear();
    public void Dispose() => ApiRegistry.Clear();

    [Fact]
    public async Task RegistryDispatchesByModelDescriptorApi()
    {
        var providerA = new FauxProvider([FauxResponseItem.Text("a")], "api-a");
        var providerB = new FauxProvider([FauxResponseItem.Text("b")], "api-b");
        ApiRegistry.Register(providerA);
        ApiRegistry.Register(providerB);

        await ApiRegistry.CompleteAsync(new ModelDescriptor("p", "m", "api-b"), new AgentContext("system", [], []), new AgentStreamOptions());

        Assert.False(providerA.WasCompleteCalled);
        Assert.True(providerB.WasCompleteCalled);
    }

    [Fact]
    public void RegistryRejectsMismatchAndMissingApiCleanly()
    {
        var registration = ApiRegistry.Register(new FauxProvider(api: "api-a"));

        Assert.Throws<InvalidOperationException>(() => registration.CompleteAsync(new ModelDescriptor("p", "m", "api-b"), new AgentContext("system", [], []), new AgentStreamOptions()).GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => ApiRegistry.Resolve("missing"));
    }

    [Fact]
    public async Task ErrorTerminalSemanticsIncludeStartAndOneErrorTerminal()
    {
        ApiRegistry.Register(new FauxProvider([FauxResponseItem.Error("boom")], "api-error"));

        var events = new List<AssistantMessageEvent>();
        await foreach (var evt in ApiRegistry.StreamAsync(new ModelDescriptor("p", "m", "api-error"), new AgentContext("system", [], []), new AgentStreamOptions())) events.Add(evt);

        Assert.IsType<AssistantMessageEvent.Start>(events.First());
        Assert.Single(events, evt => evt is AssistantMessageEvent.Error);
        Assert.Single(events, evt => evt is AssistantMessageEvent.Done or AssistantMessageEvent.Error);
    }

    [Fact]
    public async Task CrossProviderHandoffKeepsRegistryDeterministic()
    {
        ApiRegistry.Register(new FauxProvider([FauxResponseItem.Text("first")], "api-first"), "source-a");
        ApiRegistry.Register(new FauxProvider([FauxResponseItem.Text("second")], "api-second"), "source-b");

        var first = await ApiRegistry.CompleteAsync(new ModelDescriptor("p", "m1", "api-first"), new AgentContext("system", [], []), new AgentStreamOptions());
        ApiRegistry.UnregisterBySource("source-a");
        var second = await ApiRegistry.CompleteAsync(new ModelDescriptor("p", "m2", "api-second"), new AgentContext("system", [], []), new AgentStreamOptions());

        Assert.Contains(first.Content, content => content is PiSharp.Abstractions.Messages.TextContent { Text: "first" });
        Assert.Contains(second.Content, content => content is PiSharp.Abstractions.Messages.TextContent { Text: "second" });
        Assert.Null(ApiRegistry.Get("api-first"));
        Assert.NotNull(ApiRegistry.Get("api-second"));
    }
}
