using PiSharp.Cli.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class DotnetToolSelfUpdateMethodTests
{
    [Fact]
    public async Task UpdateAsync_RunsDotnetToolUpdate()
    {
        var runner = new FakeProcessRunner(0, "Tool 'pisharp' was successfully updated to version '1.2.3'.");
        var method = new DotnetToolSelfUpdateMethod(runner);

        var result = await method.UpdateAsync(addSource: null, offline: false, CancellationToken.None);

        Assert.True(result.Updated);
        Assert.Equal("1.2.3", result.InstalledVersion);
        Assert.Equal("dotnet", runner.LastFileName);
        Assert.Contains("tool update --global PiSharp.Cli", runner.LastArguments);
    }

    [Fact]
    public async Task UpdateAsync_AppendsAddSourceWhenProvided()
    {
        var runner = new FakeProcessRunner(0, "Tool 'pisharp' was successfully updated to version '1.2.3'.");
        var method = new DotnetToolSelfUpdateMethod(runner);

        await method.UpdateAsync(addSource: "https://feed.local/v3/index.json", offline: false, CancellationToken.None);

        Assert.Contains("--add-source \"https://feed.local/v3/index.json\"", runner.LastArguments);
    }

    [Fact]
    public async Task UpdateAsync_DetectsAlreadyUpToDate()
    {
        var runner = new FakeProcessRunner(0, "Tool 'pisharp' is already up to date.");
        var method = new DotnetToolSelfUpdateMethod(runner);

        var result = await method.UpdateAsync(addSource: null, offline: false, CancellationToken.None);

        Assert.False(result.Updated);
        Assert.True(result.AlreadyUpToDate);
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenToolFails()
    {
        var runner = new FakeProcessRunner(1, "", "network error");
        var method = new DotnetToolSelfUpdateMethod(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => method.UpdateAsync(null, false, CancellationToken.None));

        Assert.Contains("network error", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_ShortCircuits_WhenOffline()
    {
        var runner = new FakeProcessRunner(0, "");
        var method = new DotnetToolSelfUpdateMethod(runner);

        var result = await method.UpdateAsync(addSource: null, offline: true, CancellationToken.None);

        Assert.False(result.Updated);
        Assert.False(runner.WasCalled);
    }

    private sealed class FakeProcessRunner(int exitCode, string stdout, string stderr = "") : IPackageProcessRunner
    {
        public string? LastFileName { get; private set; }
        public string? LastArguments { get; private set; }
        public bool WasCalled { get; private set; }

        public Task RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessRunResult> RunCaptureAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastFileName = fileName;
            LastArguments = arguments;
            return Task.FromResult(new ProcessRunResult(exitCode, stdout, stderr));
        }
    }
}
