using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiHostStateSharingTests
{
    [Fact]
    public void HandlerAndRendererSeeSameStateThroughSharedDelegates()
    {
        var state = TuiRenderState.Empty("sid", "file", new ModelDescriptor("test", "m", "test"), ThinkingLevel.Off, null);
        var getState = () => state;
        var setState = (TuiRenderState s) => state = s;

        var handlerNewState = getState().AppendSystem("handler wrote this");
        setState(handlerNewState);

        Assert.Contains(getState().Transcript, item => item.Text == "handler wrote this");
    }

    [Fact]
    public void MultipleDelegatesReferenceSameStateInstance()
    {
        var state = TuiRenderState.Empty("sid", "file", new ModelDescriptor("test", "m", "test"), ThinkingLevel.Off, null);

        var handlerGet = () => state;
        var handlerSet = (TuiRenderState s) => state = s;
        var rendererGet = () => state;

        var updated = handlerGet().AppendSystem("render path should see this");
        handlerSet(updated);

        Assert.Contains(rendererGet().Transcript, item => item.Text == "render path should see this");
    }

    [Fact]
    public void UpdateThroughSetterIsVisibleAcrossAllReaders()
    {
        var state = TuiRenderState.Empty("sid", "file", new ModelDescriptor("test", "m", "test"), ThinkingLevel.Off, null);

        var reader1 = () => state;
        var reader2 = () => state;
        var writer = (TuiRenderState s) => state = s;

        var intermediate = reader1().AppendSystem("message via writer");
        writer(intermediate);

        Assert.Contains(reader1().Transcript, item => item.Text == "message via writer");
        Assert.Contains(reader2().Transcript, item => item.Text == "message via writer");
    }

    [Fact]
    public void ReduceThroughHarnessPathUpdatesRenderGet()
    {
        var state = TuiRenderState.Empty("sid", "file", new ModelDescriptor("test", "m", "test"), ThinkingLevel.Off, null);

        var harnessGet = () => state;
        var harnessSet = (TuiRenderState s) => state = s;
        var renderGet = () => state;

        var reduced = harnessGet().Reduce(
            new PiSharp.Agent.Core.Events.AgentHarnessEvent.Core(
                new PiSharp.Agent.Core.Events.AgentEvent.TurnStart()));
        harnessSet(reduced);

        Assert.True(renderGet().IsBusy);
        Assert.Equal("Thinking", renderGet().Status);
    }
}
