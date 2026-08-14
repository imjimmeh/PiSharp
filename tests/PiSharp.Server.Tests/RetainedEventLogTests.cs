using PiSharp.Agent.Core.Events;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using Xunit;

namespace PiSharp.Server.Tests;

public sealed class RetainedEventLogTests
{
    [Fact]
    public void ReplayFromSequence_ReturnsOnlyAtOrAfter()
    {
        var log = new RetainedEventLog(capacity: 4);
        log.Append(Envelope(1)); log.Append(Envelope(2)); log.Append(Envelope(3)); log.Append(Envelope(4));

        var replayed = log.ReplayFrom(2).Events.Select(e => e.Sequence);

        Assert.Equal(new long[] { 2, 3, 4 }, replayed);
    }

    [Fact]
    public void ReplayFrom_OldestDropped_ReportsGap()
    {
        var log = new RetainedEventLog(capacity: 4);
        for (var i = 1; i <= 6; i++) log.Append(Envelope(i));

        var result = log.ReplayFrom(1);

        Assert.True(result.Gap);          // sequence 1..2 evicted
        Assert.Equal(3, result.Events[0].Sequence);
        Assert.Equal(3, result.FromSequence); // clamped to the oldest retained sequence
    }

    [Fact]
    public void HeadSequence_TracksLatest()
    {
        var log = new RetainedEventLog(capacity: 4);
        log.Append(Envelope(7));
        Assert.Equal(7, log.HeadSequence);
    }

    private static ServerEventEnvelope Envelope(long sequence)
        => ServerEventEnvelope.FromFlat("srv_test", sequence, AgentSessionEvent.FromCore(new AgentEvent.AgentStart()));
}
