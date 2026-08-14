using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai.Models;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Providers.Faux;
using PiSharp.Ai.Registry;
using Xunit;

namespace PiSharp.Ai.Tests.Providers;

public sealed class BuiltInProvidersTests : IDisposable
{
    public BuiltInProvidersTests() => ApiRegistry.Clear();
    public void Dispose() => ApiRegistry.Clear();

    [Fact]
    public void BuiltInRegistrationRegistersExpectedProviderApisWithBuiltInSource()
    {
        BuiltInProviders.RegisterAll();

        foreach (var api in BuiltInProviders.ApiNames)
        {
            var registration = ApiRegistry.Get(api);
            Assert.NotNull(registration);
            Assert.Equal(BuiltInProviders.SourceId, registration!.SourceId);
        }
    }

    [Fact]
    public void CopilotChatProviderIsRegisteredAsBuiltIn()
    {
        BuiltInProviders.RegisterAll();

        var registration = ApiRegistry.Get("github-copilot-chat");
        Assert.NotNull(registration);
        Assert.Equal(BuiltInProviders.SourceId, registration!.SourceId);
        Assert.IsType<PiSharp.Ai.Providers.Copilot.GitHubCopilotProvider>(registration.Provider);
    }

    [Fact]
    public void BuiltInRegistrationsCanBeClearedWithoutRemovingExtensionProviders()
    {
        ApiRegistry.Register(new FauxProvider(api: "extension-api"), "extension");
        BuiltInProviders.RegisterAll();

        var removed = BuiltInProviders.Clear();

        Assert.Equal(BuiltInProviders.ApiNames.Count, removed);
        Assert.NotNull(ApiRegistry.Get("extension-api"));
        Assert.DoesNotContain(ApiRegistry.List(), registration => registration.SourceId == BuiltInProviders.SourceId);
    }

    [Fact]
    public async Task PublicApiResolvesCatalogModelsBeforeDispatchAndSupportsSimpleCalls()
    {
        BuiltInProviders.RegisterAll();
        var descriptor = new ModelDescriptor("faux-test", "faux-model", FauxProvider.DefaultApi);
        ModelRegistry.AddModel(new CatalogModel("faux-test", "faux-model", descriptor));

        var message = await PublicApi.CompleteSimpleAsync("faux-test", "faux-model", "hello");

        Assert.Contains(message.Content, content => content is TextContent { Text: "ok" });
    }

    [Fact]
    public void PublicApiRejectsUnknownProviderModelPairsCleanly()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PublicApi.ResolveCatalogModel("missing", "model"));

        Assert.Contains("missing/model", error.Message);
    }
}
