using PiSharp.PluginHost;
using PiSharp.Coordination;
using Xunit;

namespace PiSharp.PluginHost.Tests;

public sealed class PluginHostTests
{
    [Fact]
    public void DiscoverReturnsExplicitPluginPaths()
    {
        var path = Path.Combine(Path.GetTempPath(), "sample.dll");
        var host = new NativePluginHost(new PluginHostOptions([], [path]));

        Assert.Contains(path, host.Discover());
    }

    [Fact]
    public void FromCwdIncludesPluginsAndPiExtensionsDirectories()
    {
        var options = PluginHostOptions.FromCwd("/repo");

        Assert.Contains(options.PluginDirectories, dir => dir.EndsWith("plugins"));
        Assert.Contains(options.PluginDirectories, dir => dir.EndsWith(Path.Combine(".pi", "extensions")));
    }

    [Fact]
    public void FromCwdIncludesGlobalExtensionsDirectory()
    {
        var home = Path.Combine(Path.GetTempPath(), "pisharp-home-" + Guid.NewGuid().ToString("N"));

        var options = PluginHostOptions.FromCwd("/repo", homeDirectory: home);

        Assert.Contains(Path.Combine(home, ".pi", "extensions"), options.PluginDirectories);
    }

    [Fact]
    public void LoadSharesHostExtensionContractsWithIsolatedPluginAssembly()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-plugin-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = typeof(CoordinationExtension).Assembly.Location;
        var pluginPath = Path.Combine(root, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, pluginPath);
        var host = new NativePluginHost(new PluginHostOptions([], [pluginPath]));

        var plugin = host.Load(pluginPath);

        Assert.Equal("pisharp.coordination", plugin.Descriptor.Id);
        Assert.IsAssignableFrom<PiSharp.Extensions.IExtension>(plugin.Extension);
    }
}
