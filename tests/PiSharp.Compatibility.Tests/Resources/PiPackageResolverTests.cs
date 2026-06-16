using PiSharp.Compatibility.Resources;
using Xunit;

namespace PiSharp.Compatibility.Tests.Resources;

public sealed class PiPackageResolverTests
{
    [Fact]
    public async Task ResolveAsyncFindsNpmPackagesInPiAgentNpmNodeModules()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-packages-" + Guid.NewGuid().ToString("N"));
        var agentDir = Directory.CreateDirectory(Path.Combine(root, "home", ".pi", "agent")).FullName;
        var packageRoot = Directory.CreateDirectory(Path.Combine(agentDir, "npm", "node_modules", "@scope", "pkg")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "repo"));

        var result = await new PiPackageResolver().ResolveAsync(["npm:@scope/pkg@1.2.3"], Path.Combine(root, "repo"), agentDir);

        var package = Assert.Single(result.Packages);
        Assert.Equal(packageRoot, package.RootPath);
        Assert.Equal("npm", package.Source);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ResolveAsyncFindsNpmPackagesInPiAgentPackagesNodeModules()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-packages-" + Guid.NewGuid().ToString("N"));
        var agentDir = Directory.CreateDirectory(Path.Combine(root, "home", ".pi", "agent")).FullName;
        var packageRoot = Directory.CreateDirectory(Path.Combine(agentDir, "packages", "node_modules", "pi-headroom")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "repo"));

        var result = await new PiPackageResolver().ResolveAsync(["npm:pi-headroom"], Path.Combine(root, "repo"), agentDir);

        var package = Assert.Single(result.Packages);
        Assert.Equal(packageRoot, package.RootPath);
        Assert.Equal("npm", package.Source);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ResolveAsyncFindsGitPackagesInPiAgentGitCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-packages-" + Guid.NewGuid().ToString("N"));
        var agentDir = Directory.CreateDirectory(Path.Combine(root, "home", ".pi", "agent")).FullName;
        var packageRoot = Directory.CreateDirectory(Path.Combine(agentDir, "git", "github.com", "sinamtz", "pi-minimax-provider")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "repo"));

        var result = await new PiPackageResolver().ResolveAsync(["git:https://github.com/sinamtz/pi-minimax-provider"], Path.Combine(root, "repo"), agentDir);

        var package = Assert.Single(result.Packages);
        Assert.Equal(packageRoot, package.RootPath);
        Assert.Equal("git", package.Source);
        Assert.Empty(result.Diagnostics);
    }
}
