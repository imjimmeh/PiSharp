using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Runtime.Tests;

public sealed class ExtensionStateServiceTests
{
    [Fact]
    public async Task GetStoreReturnsFileBackedStoreAtCorrectRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-state-svc-" + Guid.NewGuid().ToString("N"));
        var userRoot = Path.Combine(root, "home", ".pi", "PiSharp", "extensions");
        var projectRoot = Path.Combine(root, "repo", ".pi", "PiSharp", "extensions");
        var service = new ExtensionStateService(userRoot, projectRoot);

        var userStore = service.GetStore("pisharp-memory", ExtensionStateScope.User);
        var projectStore = service.GetStore("pisharp-memory", ExtensionStateScope.Project);

        await userStore.SetAsync("k", "user-value");
        await projectStore.SetAsync("k", "project-value");

        // Same store is cached per (namespace, scope).
        Assert.Same(userStore, service.GetStore("pisharp-memory", ExtensionStateScope.User));
        Assert.Same(projectStore, service.GetStore("pisharp-memory", ExtensionStateScope.Project));

        // User and project are isolated files.
        Assert.True(File.Exists(Path.Combine(userRoot, "pisharp-memory", "state.json")));
        Assert.True(File.Exists(Path.Combine(projectRoot, "pisharp-memory", "state.json")));
        Assert.Equal("user-value", await userStore.GetAsync("k"));
        Assert.Equal("project-value", await projectStore.GetAsync("k"));
    }

    [Fact]
    public void EmptyNamespaceThrows()
    {
        var service = new ExtensionStateService("u", "p");
        Assert.Throws<ArgumentException>(() => service.GetStore("", ExtensionStateScope.User));
    }
}
