using PiSharp.Server.Contracts;
using Xunit;

namespace PiSharp.Sdk.Tests;

public sealed class PiSharpClientTests
{
    [Fact]
    public async Task ConnectAsync_WithCompatibleLease_ConnectsAndReportsStatus()
    {
        await using var host = await SdkTestHost.StartAsync();

        Assert.NotNull(host.Client.Lease);
        Assert.Equal(host.Host.Port, host.Client.Lease.Port);
        Assert.Equal(SdkTestHost.ApiKey, host.Client.Lease.ApiKey);
        Assert.True(host.Client.IsLocalHost);
        Assert.Equal("1", host.Client.ServerProtocolVersion);

        var status = await host.Client.DaemonStatusAsync();
        Assert.True(status.Available);
        Assert.Equal(host.Host.Port, status.Port);
        Assert.Equal("1", status.ProtocolVersion);
    }

    [Fact]
    public async Task ConnectAsync_NoLease_ThrowsWhenAutoStartDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-sdk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await Assert.ThrowsAsync<SdkException>(() => PiSharpClient.ConnectAsync(new PiSharpClientOptions
        {
            Cwd = root,
            LeaseDirectory = root,
            AutoStartDaemon = false,
        }));
    }

    [Fact]
    public async Task ConnectAsync_IncompatibleLease_IsIgnored()
    {
        await using var host = await SdkTestHost.StartAsync();

        // A lease claiming a different runtime is treated as unusable, so AutoStartDaemon=false fails.
        var store = new PiSharp.Client.DaemonLeaseStore(host.LeaseDirectory);
        await store.WriteAsync(new PiSharp.Client.DaemonLease(
            Environment.ProcessId,
            host.Host.Port,
            SdkTestHost.ApiKey,
            DateTimeOffset.UtcNow,
            "99.0"));

        await Assert.ThrowsAsync<SdkException>(() => PiSharpClient.ConnectAsync(new PiSharpClientOptions
        {
            Cwd = host.Root,
            LeaseDirectory = host.LeaseDirectory,
            AutoStartDaemon = false,
        }));
    }

    [Fact]
    public async Task CreateSession_ReturnsCreatedSession()
    {
        await using var host = await SdkTestHost.StartAsync();

        var created = await host.CreateSessionAsync("sdk-itest");

        Assert.False(string.IsNullOrWhiteSpace(created.ServerSessionId));
        Assert.NotNull(created.State);
        Assert.Equal(created.ServerSessionId, created.State.ServerSessionId);
        Assert.Equal(host.Root, created.State.Cwd);
    }

    [Fact]
    public async Task ListSessions_FindsCreatedSession()
    {
        await using var host = await SdkTestHost.StartAsync();
        var created = await host.CreateSessionAsync("sdk-list");

        // A fresh runtime session defers its JSONL file until the first user message, so
        // materialize one with a fork: forks persist immediately (header write on create),
        // which is what the list_sessions repo scans.
        ServerSessionState? fork = null;
        await using (var session = await host.Client.AttachAsync(created.ServerSessionId))
        {
            fork = await session.ForkAsync(newSessionId: "sdk-fork");
        }
        Assert.NotNull(fork);

        var result = await host.Client.ListSessionsAsync(cwd: host.Root, sessionsRoot: host.SessionsRoot);

        var persisted = Assert.Single(result.Sessions, s => s.Id == "sdk-fork");
        Assert.True(persisted.IsLive);
        Assert.Equal(created.ServerSessionId, persisted.ServerSessionId);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var host = await SdkTestHost.StartAsync();

        await host.Client.DisposeAsync();
        await host.Client.DisposeAsync(); // client dispose is idempotent

        // The server host's StopAsync is not idempotent (peer-owned), so dispose it exactly once.
        await host.DisposeAsync();
    }
}
