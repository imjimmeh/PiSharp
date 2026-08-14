using PiSharp.Cli.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class SelfUpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_ReturnsInfo_WhenNewerStableAvailable()
    {
        var checker = new SelfUpdateChecker(new FakeRegistry("2.0.0"));

        var result = await checker.CheckAsync("1.0.0", offline: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(("1.0.0", "2.0.0"), (result!.InstalledVersion, result.LatestVersion));
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenUpToDate()
    {
        var checker = new SelfUpdateChecker(new FakeRegistry("1.0.0"));

        var result = await checker.CheckAsync("1.0.0", offline: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenOffline()
    {
        var checker = new SelfUpdateChecker(new FakeRegistry("9.9.9"));

        var result = await checker.CheckAsync("1.0.0", offline: true, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenInstalledIsPrerelease()
    {
        var checker = new SelfUpdateChecker(new FakeRegistry("2.0.0"));

        var result = await checker.CheckAsync("1.5.0-beta.1", offline: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenInstalledIsMissing()
    {
        var checker = new SelfUpdateChecker(new FakeRegistry("2.0.0"));

        var result = await checker.CheckAsync("", offline: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenRegistryHasNoStableVersion()
    {
        var checker = new SelfUpdateChecker(new FakeRegistry(null));

        var result = await checker.CheckAsync("1.0.0", offline: false, CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class FakeRegistry(string? version) : INuGetRegistryClient
    {
        public Task<string?> GetLatestStableVersionAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(version);
    }
}
