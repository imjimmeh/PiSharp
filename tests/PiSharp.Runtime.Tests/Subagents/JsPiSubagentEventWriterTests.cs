using System.Text;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Serialization;
using PiSharp.Runtime.Subagents;
using Xunit;

namespace PiSharp.Runtime.Tests.Subagents;

public sealed class JsPiSubagentEventWriterTests
{
    [Fact]
    public void WriteProducesNewlineDelimitedJsonWithTurnStart()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var eventWriter = new JsPiSubagentEventWriter(writer);
        var events = JsPiSubagentEventTranslator.Translate(new AgentEvent.TurnStart());

        eventWriter.Write(events);

        var output = sb.ToString();
        Assert.Contains("\"type\":\"turn_start\"", output);
        Assert.EndsWith("\n", output);
    }

    [Fact]
    public async Task WriteAsyncProducesNewlineDelimitedJsonWithTurnStart()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var eventWriter = new JsPiSubagentEventWriter(writer);
        var events = JsPiSubagentEventTranslator.Translate(new AgentEvent.TurnStart());

        await eventWriter.WriteAsync(events);

        var output = sb.ToString();
        Assert.Contains("\"type\":\"turn_start\"", output);
        Assert.EndsWith("\n", output);
    }

    [Fact]
    public void LeaveOpenTrueLeavesWriterUsableAfterDispose()
    {
        var sb = new StringBuilder();
        var writer = new StringWriter(sb);
        var eventWriter = new JsPiSubagentEventWriter(writer, leaveOpen: true);

        eventWriter.Dispose();

        writer.Write("still-open");
        Assert.Equal("still-open", sb.ToString());
    }

    [Fact]
    public void ConstructorNullWriterThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new JsPiSubagentEventWriter(null!));
    }

    [Fact]
    public void WriteAfterDisposeThrowsObjectDisposedException()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var eventWriter = new JsPiSubagentEventWriter(writer);
        eventWriter.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            eventWriter.Write(JsPiSubagentEventTranslator.Translate(new AgentEvent.TurnStart())));
    }

    [Fact]
    public async Task WriteAsyncWithCancelledTokenThrowsOperationCanceledException()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var eventWriter = new JsPiSubagentEventWriter(writer);
        var events = JsPiSubagentEventTranslator.Translate(new AgentEvent.TurnStart());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            eventWriter.WriteAsync(events, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void RepeatedDisposeIsSafe()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var eventWriter = new JsPiSubagentEventWriter(writer);

        eventWriter.Dispose();
        var ex = Record.Exception(() => eventWriter.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public async Task RepeatedDisposeAsyncIsSafe()
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var eventWriter = new JsPiSubagentEventWriter(writer);

        await eventWriter.DisposeAsync();
        var ex = await Record.ExceptionAsync(() => eventWriter.DisposeAsync().AsTask());

        Assert.Null(ex);
    }
}
