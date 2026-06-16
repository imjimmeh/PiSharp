using PiSharp.Cli.Packages;
using PiSharp.Compatibility.Resources;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public class NpmOutdatedCheckerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public NpmOutdatedCheckerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string CreateNpmPackage(string name, string version)
    {
        var dir = Path.Combine(_tempDir, name.Replace("/", "_").Replace("@", ""));
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "package.json"),
            $$"""{"name":"{{name}}","version":"{{version}}"}""");
        return dir;
    }

    [Fact]
    public async Task CheckAsync_ReturnsOutdated_WhenNewerVersionExists()
    {
        var rootPath = CreateNpmPackage("my-plugin", "1.0.0");
        var packages = new[] { new PiResolvedPackage("npm:my-plugin", rootPath, "npm") };
        var registry = new FakeNpmRegistryClient { { "my-plugin", "2.0.0" } };
        var checker = new NpmOutdatedChecker(registry);

        var result = await checker.CheckAsync(packages);

        var item = Assert.Single(result);
        Assert.Equal("my-plugin", item.Name);
        Assert.Equal("1.0.0", item.InstalledVersion);
        Assert.Equal("2.0.0", item.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_ExcludesUpToDatePackages()
    {
        var rootPath = CreateNpmPackage("my-plugin", "2.0.0");
        var packages = new[] { new PiResolvedPackage("npm:my-plugin", rootPath, "npm") };
        var registry = new FakeNpmRegistryClient { { "my-plugin", "2.0.0" } };
        var checker = new NpmOutdatedChecker(registry);

        var result = await checker.CheckAsync(packages);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CheckAsync_ExcludesPinnedPackages()
    {
        var rootPath = CreateNpmPackage("my-plugin", "1.0.0");
        // "npm:my-plugin@1.0.0" has IsPinned = true
        var packages = new[] { new PiResolvedPackage("npm:my-plugin@1.0.0", rootPath, "npm") };
        var registry = new FakeNpmRegistryClient { { "my-plugin", "2.0.0" } };
        var checker = new NpmOutdatedChecker(registry);

        var result = await checker.CheckAsync(packages);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CheckAsync_ExcludesNonNpmPackages()
    {
        var rootPath = CreateNpmPackage("my-plugin", "1.0.0");
        // Source = "git" — not npm
        var packages = new[] { new PiResolvedPackage("git:https://github.com/x/y", rootPath, "git") };
        var registry = new FakeNpmRegistryClient { { "my-plugin", "2.0.0" } };
        var checker = new NpmOutdatedChecker(registry);

        var result = await checker.CheckAsync(packages);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CheckAsync_ReturnsEmpty_WhenRegistryReturnsNull()
    {
        var rootPath = CreateNpmPackage("my-plugin", "1.0.0");
        var packages = new[] { new PiResolvedPackage("npm:my-plugin", rootPath, "npm") };
        var registry = new FakeNpmRegistryClient(); // no entries — returns null
        var checker = new NpmOutdatedChecker(registry);

        var result = await checker.CheckAsync(packages);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CheckAsync_ReturnsEmpty_WhenPackageJsonMissing()
    {
        var dir = Path.Combine(_tempDir, "no-json-pkg");
        Directory.CreateDirectory(dir);
        // No package.json
        var packages = new[] { new PiResolvedPackage("npm:my-plugin", dir, "npm") };
        var registry = new FakeNpmRegistryClient { { "my-plugin", "2.0.0" } };
        var checker = new NpmOutdatedChecker(registry);

        var result = await checker.CheckAsync(packages);

        Assert.Empty(result);
    }
}

internal sealed class FakeNpmRegistryClient : INpmRegistryClient, IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, string> _versions = new(StringComparer.Ordinal);

    public void Add(string packageName, string version) => _versions[packageName] = version;

    public Task<string?> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken = default)
        => Task.FromResult(_versions.TryGetValue(packageName, out var v) ? v : null);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _versions.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
