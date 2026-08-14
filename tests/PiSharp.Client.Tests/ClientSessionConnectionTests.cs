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
    public async Task ConnectAsync_ForwardsToTransport()
    {
        var transport = new FakeTransport();
        await using var conn = new ClientSessionConnection(transport);
        var uri = new Uri("ws://127.0.0.1:7878/ws");
        await conn.ConnectAsync(uri, "secret-key");
        Assert.Equal(uri, transport.LastUri);
        Assert.Equal("secret-key", transport.LastApiKey);
    }

    private static ServerEventEnvelope Envelope(long sequence)
        => ServerEventEnvelope.FromFlat("srv_test", sequence, AgentSessionEvent.FromCore(new AgentEvent.AgentStart()));
}

public sealed class FakeTransport : IClientTransport
{
    public Channel<ServerEventEnvelope> Events { get; } = Channel.CreateUnbounded<ServerEventEnvelope>();
    public ServerCommandEnvelope? LastCommand { get; private set; }
    public Uri? LastUri { get; private set; }
    public string? LastApiKey { get; private set; }

    ChannelReader<ServerEventEnvelope> IClientTransport.Events => Events.Reader;

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        LastUri = uri;
        LastApiKey = apiKey;
        return Task.CompletedTask;
    }

    public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, CancellationToken ct)
    {
        LastCommand = envelope;
        return Task.FromResult(ServerResponse.Ok(envelope.Id, envelope.Type));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
