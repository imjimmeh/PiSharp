using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai.Models;
using Xunit;

namespace PiSharp.Ai.Tests.Models;

public sealed class ModelCatalogTests : IDisposable
{
    public ModelCatalogTests() => ModelRegistry.ResetToBuiltIns();

    public void Dispose() => ModelRegistry.ResetToBuiltIns();

    [Fact]
    public void GetModelReturnsMatchingProviderAndId()
    {
        var model = ModelRegistry.GetModel("anthropic", "claude-sonnet-4-5");

        Assert.NotNull(model);
        Assert.Equal("anthropic", model.Provider);
        Assert.Equal("claude-sonnet-4-5", model.Id);
        Assert.Equal("anthropic-messages", model.Descriptor.Api);
        Assert.True(model.Descriptor.Reasoning);
    }

    [Fact]
    public void GetModelReturnsNullForUnknownModel()
    {
        Assert.Null(ModelRegistry.GetModel("unknown", "nonexistent"));
    }

    [Fact]
    public void GetProvidersReturnsAllKnownProviders()
    {
        var providers = ModelRegistry.GetProviders();

        Assert.Contains("anthropic", providers);
        Assert.Contains("openai", providers);
        Assert.Contains("google", providers);
        Assert.Contains("google-vertex", providers);
        Assert.Contains("amazon-bedrock", providers);
        Assert.Contains("mistral", providers);
    }

    [Fact]
    public void GetModelsReturnsModelsForProvider()
    {
        var models = ModelRegistry.GetModels("openai");

        Assert.True(models.Count >= 2);
        Assert.Contains(models, m => m.Id == "gpt-4o");
        Assert.Contains(models, m => m.Id == "o3");
    }

    [Fact]
    public void CalculateCostMatchesPerMillionRates()
    {
        var model = ModelRegistry.GetModel("anthropic", "claude-sonnet-4-5")!.Descriptor;
        var usage = new UsageInfo(Input: 1_000_000, Output: 1_000_000, CacheRead: 1_000_000, CacheWrite: 1_000_000);

        var cost = ModelRegistry.CalculateCost(model, usage);

        Assert.Equal(3m, cost.Input);
        Assert.Equal(15m, cost.Output);
        Assert.Equal(0.3m, cost.CacheRead);
        Assert.Equal(3.75m, cost.CacheWrite);
        Assert.Equal(3m + 15m + 0.3m + 3.75m, cost.Total);
    }

    [Fact]
    public void NonReasoningModelOnlySupportsOff()
    {
        var model = ModelRegistry.GetModel("openai", "gpt-4o")!.Descriptor;

        var levels = ModelRegistry.GetSupportedThinkingLevels(model);

        Assert.Equal([ThinkingLevel.Off], levels);
    }

    [Fact]
    public void ReasoningModelSupportsCurrentMappedLevels()
    {
        var model = ModelRegistry.GetModel("amazon-bedrock", "anthropic.claude-haiku-4-5-20251001-v1:0")!.Descriptor;

        var levels = ModelRegistry.GetSupportedThinkingLevels(model);

        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh], levels);
    }

    [Fact]
    public void ClampPreservesSupportedLevel()
    {
        var model = ModelRegistry.GetModel("amazon-bedrock", "anthropic.claude-haiku-4-5-20251001-v1:0")!.Descriptor;

        var clamped = ModelRegistry.ClampThinkingLevel(model, ThinkingLevel.Low);

        Assert.Equal(ThinkingLevel.Low, clamped);
    }

    [Fact]
    public void GetCustomProvidersReturnsEmptyForBuiltInsOnly()
    {
        var custom = ModelRegistry.GetCustomProviders();

        Assert.Empty(custom);
    }

    [Fact]
    public void GetCustomProvidersReturnsCustomProviderAfterRegisterConfig()
    {
        ModelRegistry.RegisterProviderConfig(new ModelProviderConfig("my-provider", "openai-responses"), "models.json");

        var custom = ModelRegistry.GetCustomProviders();

        Assert.Contains("my-provider", custom);
    }

    [Fact]
    public void GetCustomProvidersExcludesBuiltInSource()
    {
        var custom = ModelRegistry.GetCustomProviders();

        Assert.DoesNotContain("openai", custom);
        Assert.DoesNotContain("anthropic", custom);
    }

    [Fact]
    public void IsProviderAccessibleReturnsTrueForCustomProvider()
    {
        var customProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "my-provider" };
        var storedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(ModelRegistry.IsProviderAccessible("my-provider", storedProviders, customProviders));
    }

    [Fact]
    public void IsProviderAccessibleReturnsTrueForStoredProvider()
    {
        var customProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "openai" };

        Assert.True(ModelRegistry.IsProviderAccessible("openai", storedProviders, customProviders));
    }

    [Fact]
    public void IsProviderAccessibleReturnsFalseForUnknownProvider()
    {
        var customProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(ModelRegistry.IsProviderAccessible("unknown-provider", storedProviders, customProviders));
    }
}
