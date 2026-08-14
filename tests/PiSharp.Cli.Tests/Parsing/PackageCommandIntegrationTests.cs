using PiSharp.Cli.IO;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Parsing;
using PiSharp.Compatibility.Settings;
using Xunit;

namespace PiSharp.Cli.Tests.Parsing;

public sealed class PackageCommandIntegrationTests
{
    [Fact]
    public async Task InstallCommandRunsBeforeRuntimeStartup()
    {
        var console = new TestConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["install", "npm:@foo/bar"],
            console,
            packageCommandRunner: runner);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task InstallCommandOutputsInstalledMessage()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["install", "npm:@foo/bar"],
            console,
            packageCommandRunner: runner);

        Assert.Contains("Installed npm:@foo/bar", console.GetOutput());
    }

    [Fact]
    public async Task RemoveCommandOutputsRemovedMessage()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner { RemoveResult = true };

        var exitCode = await Program.RunAsync(
            ["remove", "npm:@foo/bar"],
            console,
            packageCommandRunner: runner);

        Assert.Contains("Removed npm:@foo/bar", console.GetOutput());
    }

    [Fact]
    public async Task RemoveCommandOutputsNoMatchMessage()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner { RemoveResult = false };

        var exitCode = await Program.RunAsync(
            ["remove", "npm:@foo/bar"],
            console,
            packageCommandRunner: runner);

        Assert.Contains("No matching package found for npm:@foo/bar", console.GetOutput());
    }

    [Fact]
    public async Task ConfigCommandWithNoPackagesOutputsEmptyMessage()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["config"],
            console,
            packageCommandRunner: runner);

        Assert.Contains("No packages installed.", console.GetOutput());
    }

    [Fact]
    public async Task ConfigCommandOutputsPackagesWithLayer()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner
        {
            ListResult = [
                new PiSharp.Cli.Packages.PackageListEntry("npm:global-pkg", PiSettingsLayer.GlobalLegacy),
                new PiSharp.Cli.Packages.PackageListEntry("npm:project-pkg", PiSettingsLayer.ProjectLegacy)
            ]
        };

        var exitCode = await Program.RunAsync(
            ["config"],
            console,
            packageCommandRunner: runner);

        Assert.Contains("Installed packages:", console.GetOutput());
        Assert.Contains("npm:global-pkg", console.GetOutput());
        Assert.Contains("npm:project-pkg", console.GetOutput());
        Assert.Contains("[user]", console.GetOutput());
        Assert.Contains("[project]", console.GetOutput());
    }

    [Fact]
    public async Task ListCommandWithNoPackagesOutputsEmptyMessage()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["list"],
            console,
            packageCommandRunner: runner);

        Assert.Contains("No packages installed.", console.GetOutput());
    }

    [Fact]
    public async Task ListCommandOutputsGroupedPackages()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner
        {
            ListResult = [
                new PiSharp.Cli.Packages.PackageListEntry("npm:global-pkg", PiSettingsLayer.GlobalLegacy),
                new PiSharp.Cli.Packages.PackageListEntry("npm:project-pkg", PiSettingsLayer.ProjectLegacy)
            ]
        };

        var exitCode = await Program.RunAsync(
            ["list"],
            console,
            packageCommandRunner: runner);

        Assert.Contains("User packages:", console.GetOutput());
        Assert.Contains("Project packages:", console.GetOutput());
        Assert.Contains("npm:global-pkg", console.GetOutput());
        Assert.Contains("npm:project-pkg", console.GetOutput());
    }

    [Fact]
    public async Task UpdateCommandRunsWithoutError()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["update"],
            console,
            packageCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.True(runner.UpdateCalled);
    }

    [Fact]
    public async Task UpdateCommandWithSourcePassesSourceToRunner()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["update", "npm:@foo/bar"],
            console,
            packageCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.Equal("npm:@foo/bar", runner.LastUpdateRequest?.Source);
    }

    [Fact]
    public async Task UpdateSelfCommandDelegatesToRunner()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["update", "--self"],
            console,
            packageCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.True(runner.UpdateCalled);
        Assert.NotNull(runner.LastUpdateRequest);
        Assert.True(runner.LastUpdateRequest.Self);
        Assert.DoesNotContain("not yet implemented", console.GetOutput(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateCommandWithOfflineFlagPassesOfflineToRunner()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["update", "--offline"],
            console,
            packageCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.NotNull(runner.LastUpdateRequest);
        Assert.True(runner.LastUpdateRequest.Offline);
    }

    [Fact]
    public async Task UpdateCommandWithForceFlagPassesForceToRunner()
    {
        var console = new TestPackageConsoleIO();
        var runner = new FakePackageCommandRunner();

        var exitCode = await Program.RunAsync(
            ["update", "npm:@foo/bar@1.2.3", "--force"],
            console,
            packageCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.NotNull(runner.LastUpdateRequest);
        Assert.True(runner.LastUpdateRequest.Force);
    }
}

public sealed class FakePackageCommandRunner : IPackageCommandRunner
{
    public bool RemoveResult { get; set; }
    public List<PiSharp.Cli.Packages.PackageListEntry> ListResult { get; set; } = [];
    public List<string> InstallCalls { get; } = [];
    public bool UpdateCalled { get; private set; }
    public PackageUpdateRequest? LastUpdateRequest { get; private set; }
    public bool ConfigCalled { get; private set; }

    public Task<bool> RemoveAsync(string source, bool local) => Task.FromResult(RemoveResult);

    public Task<List<PackageListEntry>> ListAsync() => Task.FromResult(ListResult);

    public Task ConfigAsync()
    {
        ConfigCalled = true;
        return Task.CompletedTask;
    }

    public Task InstallAsync(string source, bool local, bool force = false, bool offline = false)
    {
        InstallCalls.Add(source);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(PackageUpdateRequest request)
    {
        UpdateCalled = true;
        LastUpdateRequest = request;
        return Task.CompletedTask;
    }
}

internal sealed class TestPackageConsoleIO : IConsoleIO
{
    private TextWriter _out = new StringWriter();
    private readonly StringWriter _error = new();

    public TestPackageConsoleIO()
    {
        IsInputRedirected = false;
    }

    public TextReader In { get; } = new StringReader("");
    public TextWriter Out => _out;
    public TextWriter Error => _error;
    public bool IsInputRedirected { get; }
    public bool IsOutputRedirected => false;
    public void SetOut(TextWriter writer) => _out = writer;

    public string GetOutput() => ((StringWriter)_out).ToString();
}
