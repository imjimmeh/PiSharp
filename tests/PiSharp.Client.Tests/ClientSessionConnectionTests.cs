using System.Threading.Channels;
using PiSharp.Agent.Core.Events;
using PiSharp.Server.Contracts;
using Xunit;

namespace PiSharp.Client.Tests;

public sealed class ClientSessionConnectionTests
{
    [Fact]
    public async Task SendCommand_SendsEnvelope_ReturnsResponse()
    {
        var transport = new FakeTransport();
        await using var conn = new ClientSessionConnection(transport);
        var response = await conn.SendAsync(new ServerCommandEnvelope(ServerCommandTypes.GetState, Id: "1", ServerSessionId: "srv_x"));
        Assert.True(response.Success);
        Assert.Equal("get_state", transport.LastCommand!.Type);
    }

    [Fact]
    public async Task Subscribe_AppliesEnvelopesInSequence()
    {
        var transport = new FakeTransport();
        await using var conn = new ClientSessionConnection(transport);
        var applied = 0L;
        conn.EventReceived += (envelope) => { applied = envelope.Sequence; };
        transport.Events.Writer.TryWrite(Envelope(1));
        transport.Events.Writer.TryWrite(Envelope(2));
        await Task.Delay(50);
        Assert.Equal(2, applied);
        Assert.Equal(2, conn.LastAppliedSequence);
    }

    [Fact]
    public async Task SendCommand_AssignsId_WhenAbsent()
    {
        var transport = new FakeTransport();
        await using var conn = new ClientSessionConnection(transport);
        var response = await conn.SendAsync(new ServerCommandEnvelope(ServerCommandTypes.GetState, ServerSessionId: "srv_x"));
        Assert.NotNull(transport.LastCommand!.Id);
        Assert.Equal(transport.LastCommand.Id, response.Id);
    }

    [Fact]
    public async Task SendCommand_WithPayload_ForwardsPayload()
    {
        var transport = new FakeTransport();
        await using var conn = new ClientSessionConnection(transport);
        await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Attach, Id: "1", ServerSessionId: "srv_x"),
            new AttachPayload(SinceSequence: 42));
        Assert.Equal(42, Assert.IsType<AttachPayload>(transport.LastPayload).SinceSequence);
    }

    private sealed record AttachPayload(long SinceSequence);

    private static ServerEventEnvelope Envelope(long sequence)
        => ServerEventEnvelope.FromFlat("srv_test", sequence, AgentSessionEvent.FromCore(new AgentEvent.AgentStart()));
}

public sealed class FakeTransport : IClientTransport
{
    public Channel<ServerEventEnvelope> Events { get; } = Channel.CreateUnbounded<ServerEventEnvelope>();
    public Channel<ServerResponse> Late { get; } = Channel.CreateUnbounded<ServerResponse>();
    public ServerCommandEnvelope? LastCommand { get; private set; }
    public object? LastPayload { get; private set; }
    public Uri? LastUri { get; private set; }
    public string? LastApiKey { get; private set; }

    ChannelReader<ServerEventEnvelope> IClientTransport.Events => Events.Reader;
    ChannelReader<ServerResponse> IClientTransport.LateResponses => Late.Reader;

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        LastUri = uri;
        LastApiKey = apiKey;
        return Task.CompletedTask;
    }

    public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, CancellationToken ct, TimeSpan? timeoutOverride = null)
        => SendCommandAsync(envelope, payload: null, ct, timeoutOverride);

    public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct, TimeSpan? timeoutOverride = null)
    {
        LastCommand = envelope;
        LastPayload = payload;
        return Task.FromResult(ServerResponse.Ok(envelope.Id, envelope.Type));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

