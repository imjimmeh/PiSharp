using PiSharp.Ai.Models;
using Xunit;

namespace PiSharp.Ai.Tests.Models;

public sealed class ModelsJsonCatalogLoaderTests : IDisposable
{
    public ModelsJsonCatalogLoaderTests() => ModelRegistry.ResetToBuiltIns();

    public void Dispose() => ModelRegistry.ResetToBuiltIns();

    [Fact]
    public void CanonicalRecipeJsonRegistersProviderConfigs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-models-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          "providers": {
            "openrouter": { "apiKey": "OPENROUTER_API_KEY" },
            "perplexity": {
              "api": "anthropic-messages",
              "baseUrl": "https://api.perplexity.ai/anthropic"
            },
            "zai": {
              "baseUrl": "https://api.z.ai/api/paas/v4/chat/completions",
              "headers": { "X-Api-Key": "ZAI_API_KEY" }
            }
          }
        }
        """);
        try
        {
            var registered = ModelsJsonCatalogLoader.Load(path);

            Assert.True(registered >= 3);

            var openrouter = ModelRegistry.GetProviderConfig("openrouter");
            Assert.NotNull(openrouter);
            Assert.Equal("OPENROUTER_API_KEY", openrouter!.ApiKey);

            var perplexity = ModelRegistry.GetProviderConfig("perplexity");
            Assert.NotNull(perplexity);
            Assert.Equal("anthropic-messages", perplexity!.Api);
            Assert.Equal("https://api.perplexity.ai/anthropic", perplexity.BaseUrl);

            var zai = ModelRegistry.GetProviderConfig("zai");
            Assert.NotNull(zai);
            Assert.Equal("https://api.z.ai/api/paas/v4/chat/completions", zai!.BaseUrl);
            Assert.Equal("ZAI_API_KEY", zai.Headers!["X-Api-Key"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileLoadsZeroModels()
    {
        Assert.Equal(0, ModelsJsonCatalogLoader.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));
    }
}
