using PiSharp.Abstractions.Options;
using PiSharp.Ai;
using PiSharp.Runtime;
using Xunit;

namespace PiSharp.Cli.Tests.Bootstrap;

public sealed class ModelSelectorTests
{
    [Fact]
    public void ResolvesProviderSlashModelAndThinkingSuffixThroughCatalog()
    {
        var descriptor = PublicApi.Models.First().Descriptor;
        var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, $"{descriptor.Provider}/{descriptor.Id}:high", null));

        Assert.Equal(descriptor.Provider, selection.Model.Provider);
        Assert.Equal(descriptor.Id, selection.Model.Id);
    }

    [Fact]
    public void ScopedModelsCanCycleDeterministically()
    {
        var models = PublicApi.Models.Take(2).Select(model => $"{model.Descriptor.Provider}/{model.Descriptor.Id}").ToArray();
        if (models.Length < 2) return;
        var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, null, null, models));

        var cycled = RuntimeModelSelector.Cycle(selection, 1);

        Assert.True(cycled.IsScoped);
        Assert.NotEqual(selection.Model.Id, cycled.Model.Id);
    }

    [Fact]
    public void ClampsThinkingForNonReasoningModel()
    {
        var model = PublicApi.Models.Select(m => m.Descriptor).First(m => !m.Reasoning);
        var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(model.Provider, model.Id, ThinkingLevel.High));

        Assert.Equal(ThinkingLevel.Off, selection.ThinkingLevel);
    }
}
