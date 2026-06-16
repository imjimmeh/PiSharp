using PiSharp.Ai.Models;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class TsProviderAdapterTests : IDisposable
{
    public TsProviderAdapterTests() => ModelRegistry.ResetToBuiltIns();

    public void Dispose() => ModelRegistry.ResetToBuiltIns();

    [Fact]
    public void DeclarativeProviderRegistersModels()
    {
        var provider = TsProviderAdapter.Create(new TsProviderConfig("custom", "custom-api", Models: [new TsProviderModel("custom", "model-1", "Model 1")]));

        Assert.Equal("custom-api", provider.Api);
        Assert.NotNull(ModelRegistry.GetModel("custom", "model-1"));
    }

    [Fact]
    public void DeclarativeRegistrationUsesExistingTransportRatherThanFakeProvider()
    {
        var provider = TsProviderAdapter.Register(new TsProviderConfig("custom", "openai-responses", Models: [new TsProviderModel("custom", "model-1", "Model 1")]), "test-source");

        Assert.Null(provider);
        Assert.Equal("openai-responses", ModelRegistry.GetModel("custom", "model-1")!.Descriptor.Api);
    }

    [Fact]
    public void CustomStreamHandlersCreateCallbackProviders()
    {
        Assert.Equal("api", TsProviderAdapter.Create(new TsProviderConfig("stream", "api", HasCustomStreamHandler: true)).Api);
    }
}
