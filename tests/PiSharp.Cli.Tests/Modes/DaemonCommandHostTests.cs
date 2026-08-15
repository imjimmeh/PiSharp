using PiSharp.Cli.Modes;
using PiSharp.Cli.Tests.Modes;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using PiSharp.Server.Runtime;
using PiSharp.Server.UiBridge;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class DaemonCommandHostTests
{
    [Fact]
    public async Task RunCommandAsyncExecutesSlashCommandsAgainstSessionRuntime()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        await using var live = new LiveServerSession("test-session", runtime);
        var bridge = new FakeUiBridge();
        var options = DaemonCommandHost.CreateHostOptions("key", resolveSession: () => live);

        var result = await options.RunCommandAsync!(
            new PiServerHostContext(live, bridge),
            "/settings",
            null,
            CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("Current settings:", result.Message);
    }

    [Fact]
    public async Task GetCommands_ReturnsSlashCommandNames()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        await using var live = new LiveServerSession("test-session", runtime);
        var options = DaemonCommandHost.CreateHostOptions("key");

        var commands = await options.GetCommandsAsync!(live, CancellationToken.None);

        Assert.Contains("/settings", commands);
        Assert.Contains("/quit", commands);
        Assert.All(commands, command => Assert.StartsWith("/", command));
    }

    [Fact]
    public void CreateHostOptionsWiresEveryCommandDelegate()
    {
        var options = DaemonCommandHost.CreateHostOptions("key");

        Assert.NotNull(options.RunCommandAsync);
        Assert.NotNull(options.CompleteCommandAsync);
        Assert.NotNull(options.ProcessInputAsync);
        Assert.NotNull(options.GetStartupMessagesAsync);
        Assert.NotNull(options.PostStartupChecksAsync);
        Assert.NotNull(options.GetMcpStatusAsync);
        Assert.NotNull(options.GetCommandsAsync);
    }

    private sealed class FakeUiBridge : IServerUiBridge
    {
        public Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, CancellationToken cancellationToken = default)
            => Task.FromResult(new ServerUiResponse(intent.RequestId));

        public Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, LiveServerSession target, TimeSpan? responseTimeout, CancellationToken ct = default)
            => Task.FromResult(new ServerUiResponse(intent.RequestId));

        public void ResolveUiAsync(string requestId, string? value, bool cancelled)
        {
        }
    }
}
