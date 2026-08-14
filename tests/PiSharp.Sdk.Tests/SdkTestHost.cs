using PiSharp.Client;
using PiSharp.Sdk;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;

namespace PiSharp.Sdk.Tests;

/// <summary>
/// In-process daemon + lease + connected <see cref="PiSharpClient"/> fixture. Mirrors
/// <c>PiSharp.Client.Tests.DaemonIntegrationTests</c>: a real <see cref="PiServerHost"/> on an
/// ephemeral port, a real lease written to a temp directory, and a real WebSocket client.
/// </summary>
internal sealed class SdkTestHost : IAsyncDisposable
{
    public const string ApiKey = "sdk-test-key";

    public PiServerHost Host { get; }
    public string Root { get; }
    public string LeaseDirectory { get; }
    public PiSharpClient Client { get; }

    private SdkTestHost(PiServerHost host, string root, string leaseDirectory, PiSharpClient client)
    {
        Host = host;
        Root = root;
        LeaseDirectory = leaseDirectory;
        Client = client;
    }

    public static async Task<SdkTestHost> StartAsync(PiServerHostOptions? hostOptions = null)
    {
        var root = NewTempDir();
        var host = new PiServerHost(hostOptions ?? new PiServerHostOptions
        {
            ApiKey = ApiKey,
            IdleTimeout = TimeSpan.FromHours(1),
        });
        await host.StartAsync(0);

        var leaseDirectory = Path.Combine(root, "lease");
        var store = new DaemonLeaseStore(leaseDirectory);
        await store.WriteAsync(new DaemonLease(
            Environment.ProcessId,
            host.Port,
            ApiKey,
            DateTimeOffset.UtcNow,
            $"{Environment.Version.Major}.{Environment.Version.Minor}"));

        var client = await PiSharpClient.ConnectAsync(new PiSharpClientOptions
        {
            Cwd = root,
            LeaseDirectory = leaseDirectory,
            AutoStartDaemon = false,
        });

        return new SdkTestHost(host, root, leaseDirectory, client);
    }

    /// <summary>Creates a fully-suppressed session (no tools/extensions/skills/themes) under a temp sessions root.</summary>
    public Task<ServerSessionCreated> CreateSessionAsync(string? sessionId = null)
        => Client.CreateSessionAsync(new CreateSessionOptions(
            Root,
            SessionId: sessionId,
            SessionsRoot: SessionsRoot,
            NoTools: true,
            NoBuiltinTools: true,
            NoExtensions: true,
            NoSkills: true,
            NoPromptTemplates: true,
            NoThemes: true,
            NoContextFiles: true));

    public string SessionsRoot => Path.Combine(Root, "sessions");

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Host.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-sdk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
