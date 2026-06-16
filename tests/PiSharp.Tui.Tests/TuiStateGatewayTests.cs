using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Shell;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiStateGatewayTests
{
    private static TuiRenderState EmptyState() => TuiRenderState.Empty(
        "sid", "file", new ModelDescriptor("test", "m", "test"), ThinkingLevel.Off, null);

    private static TuiFooterSnapshot EmptyFooterSnapshot()
        => new("", null, 0, 0, 0, 0, 0m, 0, 0, false, new Dictionary<string, string>());

    [Fact]
    public void UpdateAppliesTransformAndSetsState()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = EmptyState();
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        var gateway = new TuiStateGateway(
            () => state, s => state = s, renderCoordinator, appContext, CancellationToken.None);

        gateway.Update(s => s.AppendSystem("hello"));

        Assert.Contains(state.Transcript, item => (item.Text ?? string.Empty).Contains("hello"));
    }

    [Fact]
    public void StatePropertyReflectsCurrentState()
    {
        var appContext = new FakeTuiApplicationContext();
        var state = EmptyState();
        var shell = new TuiShellView();
        var renderCoordinator = new TuiRenderCoordinator(
            shell, () => state, s => state = s, appContext, EmptyFooterSnapshot);
        var gateway = new TuiStateGateway(
            () => state, s => state = s, renderCoordinator, appContext, CancellationToken.None);

        var before = gateway.State;
        gateway.Update(s => s.AppendSystem("world"));

        Assert.NotSame(before, gateway.State);
    }
}
