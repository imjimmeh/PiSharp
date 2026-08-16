using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Cli.Modes;
using PiSharp.Client;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class RemoteTuiE2ETestFixture : IAsyncDisposable
{
    public string ApiKey { get; } = "e2e-secret-key-" + Guid.NewGuid().ToString("N");
    public string WorkingDirectory { get; }
    public PiServerHost DaemonHost { get; private set; } = null!;
    public ClientSessionConnection ClientConnection { get; private set; } = null!;
    public RemoteTuiBackend Backend { get; private set; } = null!;
    internal RunningTuiHost RunningTui { get; private set; } = null!;

    public string ScreenText => RunningTui.ScreenText;

    private RemoteTuiE2ETestFixture(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    public static async Task<RemoteTuiE2ETestFixture> StartAsync(
        string? assistantResponse = "Hello from daemon!",
        int width = 120,
        int height = 40)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var fixture = new RemoteTuiE2ETestFixture(root);

        LiveServerSession? activeSession = null;

        // 1. Start live daemon host with full production DaemonCommandHost options
        var hostOptions = DaemonCommandHost.CreateHostOptions(
            apiKey: fixture.ApiKey,
            loggerFactory: NullLoggerProvider.Instance != null ? null : null,
            resolveSession: () => activeSession);

        fixture.DaemonHost = new PiServerHost(hostOptions);
        await fixture.DaemonHost.StartAsync(0);

        // 2. Connect client WebSocket transport
        var transport = new ClientWebSocketTransport(NullLogger.Instance, TimeSpan.FromSeconds(10));
        fixture.ClientConnection = new ClientSessionConnection(transport, NullLogger.Instance);
        await fixture.ClientConnection.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.DaemonHost.Port}/"), fixture.ApiKey, CancellationToken.None);

        // 3. Create server session over WebSocket
        var createResp = await fixture.ClientConnection.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession),
            new { cwd = root },
            CancellationToken.None);
        Assert.True(createResp.Success, createResp.Error?.Message);

        var sessionId = ((JsonElement)createResp.Data!).GetProperty("serverSessionId").GetString()!;

        // Capture live session reference for process_input resolution
        activeSession = fixture.DaemonHost.Registry.TryGet(sessionId, out var sessionRef) ? sessionRef : null;

        // 4. Attach to session event stream
        var attachResp = await fixture.ClientConnection.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Attach, ServerSessionId: sessionId),
            new { sinceSequence = 0L },
            CancellationToken.None);
        Assert.True(attachResp.Success, attachResp.Error?.Message);

        // 5. Construct RemoteTuiBackend
        fixture.Backend = new RemoteTuiBackend(fixture.ClientConnection, NullLogger.Instance)
        {
            ServerSessionId = sessionId
        };

        // 6. Start RunningTuiHost via TuiIntegrationTestHost
        fixture.RunningTui = await TuiIntegrationTestHost.StartAsync(
            runtime: fixture.Backend,
            width: width,
            height: height,
            dispatchCommandAsync: fixture.Backend.DispatchCommandAsync,
            getSessionSnapshotAsync: null,
            processInputAsync: fixture.Backend.ProcessInputAsync);

        return fixture;
    }

    public Task SubmitPromptAsync(string text) => RunningTui.SubmitPromptAsync(text);

    public async ValueTask DisposeAsync()
    {
        if (RunningTui is not null) await RunningTui.StopAsync();
        if (Backend is not null) await Backend.DisposeAsync();
        if (DaemonHost is not null) await DaemonHost.DisposeAsync();
        if (Directory.Exists(WorkingDirectory))
        {
            try { Directory.Delete(WorkingDirectory, true); } catch { }
        }
    }
}
