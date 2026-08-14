using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// Verifies the IExtensionModelApi role surface: the two default interface
/// members (ResolveRoleAsync/SetModelByRoleAsync) fall back to a null / false
/// no-op when a host does not wire role binding, so existing implementors keep
/// working unchanged. Also verifies the ExtensionModelSelection contract record
/// carries model + thinking.
/// </summary>
public sealed class ExtensionModelRoleApiTests
{
    private sealed class MinimalModelApi : IExtensionModelApi
    {
        public Task<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default) => Task.FromResult<ThinkingLevel?>(ThinkingLevel.Medium);
        public Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ResolveRoleAsync_DefaultsToNull_WhenNotBound()
    {
        IExtensionModelApi api = new MinimalModelApi();
        var result = await api.ResolveRoleAsync("fast_worker", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetModelByRoleAsync_DefaultsToFalse_WhenNotBound()
    {
        IExtensionModelApi api = new MinimalModelApi();
        var result = await api.SetModelByRoleAsync("fast_worker", CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ExistingModelSurface_StillWorks()
    {
        IExtensionModelApi api = new MinimalModelApi();
        Assert.True(await api.SetModelAsync(new ModelDescriptor("prov", "id", "api"), CancellationToken.None));
        Assert.Equal(ThinkingLevel.Medium, await api.GetThinkingLevelAsync(CancellationToken.None));
        await api.SetThinkingLevelAsync(ThinkingLevel.Low, CancellationToken.None);
    }

    [Fact]
    public void ExtensionModelSelection_CarriesModelAndThinking()
    {
        var descriptor = new ModelDescriptor("prov", "id", "api");
        var selection = new ExtensionModelSelection(descriptor, ThinkingLevel.High);
        Assert.Same(descriptor, selection.Model);
        Assert.Equal(ThinkingLevel.High, selection.ThinkingLevel);
    }
}
