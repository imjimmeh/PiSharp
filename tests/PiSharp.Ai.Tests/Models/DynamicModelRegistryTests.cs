using PiSharp.Agent.Core.Models;
using PiSharp.Ai.Models;
using Xunit;

namespace PiSharp.Ai.Tests.Models;

public sealed class DynamicModelRegistryTests : IDisposable
{
    public DynamicModelRegistryTests() => ModelRegistry.ResetToBuiltIns();

    public void Dispose() => ModelRegistry.ResetToBuiltIns();

    [Fact]
    public void DynamicModelOverridesAndUnregisterRestoresBuiltInModel()
    {
        var original = ModelRegistry.GetModel("openai", "gpt-4o")!.Descriptor;
        var replacement = original with { Name = "GPT override", BaseUrl = "https://proxy.example.com" };

        ModelRegistry.RegisterModel(new CatalogModel("openai", "gpt-4o", replacement), "test-source");

        Assert.Equal("GPT override", ModelRegistry.GetModel("openai", "gpt-4o")!.Descriptor.Name);

        ModelRegistry.UnregisterBySource("test-source");

        var restored = ModelRegistry.GetModel("openai", "gpt-4o")!.Descriptor;
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.BaseUrl, restored.BaseUrl);
    }

    [Fact]
    public void ModelsJsonRegistersCustomProviderAndOverrideOnlyProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-models-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          "providers": {
            "custom-proxy": {
              "api": "openai-responses",
              "baseUrl": "https://proxy.example.com/v1",
              "apiKey": "PROXY_API_KEY",
              "headers": { "x-proxy": "1" },
              "models": [
                { "id": "model-1", "name": "Proxy Model", "contextWindow": 1234, "maxTokens": 5678, "input": ["text"] }
              ]
            },
            "openai": {
              "baseUrl": "https://openai-proxy.example.com",
              "headers": { "x-openai-proxy": "1" }
            }
          }
        }
        """);

        try
        {
            var loaded = ModelsJsonCatalogLoader.Load(path, "models-json-test");

            Assert.True(loaded >= 3);
            var custom = ModelRegistry.GetModel("custom-proxy", "model-1")!.Descriptor;
            Assert.Equal("openai-responses", custom.Api);
            Assert.Equal("https://proxy.example.com/v1", custom.BaseUrl);
            Assert.Equal("PROXY_API_KEY", custom.ApiKey);
            Assert.Equal("1", custom.Headers!["x-proxy"]);

            var overridden = ModelRegistry.GetModel("openai", "gpt-4o")!.Descriptor;
            Assert.Equal("https://openai-proxy.example.com", overridden.BaseUrl);
            Assert.Equal("1", overridden.Headers!["x-openai-proxy"]);

            ModelRegistry.UnregisterBySource("models-json-test");

            Assert.Null(ModelRegistry.GetModel("custom-proxy", "model-1"));
            Assert.NotEqual("https://openai-proxy.example.com", ModelRegistry.GetModel("openai", "gpt-4o")!.Descriptor.BaseUrl);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
