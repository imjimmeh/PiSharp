using System.Text.Json;
using PiSharp.Client;
using PiSharp.Cli.Modes;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

/// <summary>
/// End-to-end proof that a standalone daemon host built through
/// <see cref="DaemonCommandHost.CreateHostOptions"/> (the exact construction
/// <see cref="DaemonMode"/> uses in <c>RunForegroundAsync</c>) wires real command delegates:
/// a create_session + run_command round trip over the real websocket transport returns
/// <c>Success</c> instead of the <c>not_available</c> failure the unwired host produces.
/// The <c>process_input</c> lane resolves its session through <see cref="PiServerHost.Registry"/>,
/// so the test also proves the launcher can reach the host's session registry.
/// </summary>
public sealed class DaemonCommandHostIntegrationTests
{
    private const string ApiKey = "itest-key";

    [Fact]
    public async Task WiredDaemonExecutesRunCommandOverRealTransport()
    {
        var root = NewTempDir();
        PiServerHost? host = null;
        var options = DaemonCommandHost.CreateHostOptions(
            ApiKey,
            resolveSession: () => host?.Registry?.Sessions.MaxBy(s => s.Id, StringComparer.Ordinal)) with
        {
            IdleTimeout = TimeSpan.FromHours(1),
        };
        host = new PiServerHost(options);
        await host.StartAsync(0);

        var transport = new ClientWebSocketTransport(TimeSpan.FromSeconds(60));
        await using var conn = new ClientSessionConnection(transport);
        await conn.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/"), ApiKey, CancellationToken.None);

        var createResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession),
            CreatePayload(root),
            CancellationToken.None);
        Assert.True(createResp.Success, createResp.Error?.Message);
        var sessionId = ((JsonElement)createResp.Data!).GetProperty("serverSessionId").GetString()!;

        // The host's Registry accessor is the same registry the server used to create the session,
        // i.e. the instance the daemon launcher's resolveSession delegate will read.
        Assert.NotNull(host.Registry);
        Assert.Single(host.Registry.Sessions);

        var runResp = await conn.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.RunCommand, ServerSessionId: sessionId),
            new { text = "/settings", options = (object?)null },
            CancellationToken.None);

        Assert.True(runResp.Success, runResp.Error?.Message);
        Assert.NotEqual("not_available", runResp.Error?.Code);
        var result = (JsonElement)runResp.Data!;
        Assert.True(result.GetProperty("handled").GetBoolean());
    }

    private static object CreatePayload(string root) => new
    {
        cwd = root,
        sessionsRoot = Path.Combine(root, "sessions"),
        noTools = true,
        noBuiltinTools = true,
        noExtensions = true,
        noSkills = true,
        noPromptTemplates = true,
        noThemes = true,
        noContextFiles = true,
    };

    private static string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-daemon-wired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
