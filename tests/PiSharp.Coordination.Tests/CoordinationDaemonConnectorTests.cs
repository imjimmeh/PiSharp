using System.Text.Json;
using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class CoordinationDaemonConnectorTests
{
    [Fact]
    public async Task ConnectorReplacesStaleDaemonMetadata()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");
        Directory.CreateDirectory(metadataDirectory);
        await File.WriteAllTextAsync(Path.Combine(metadataDirectory, "daemon.json"), """
        {"processId":999999,"pipeName":"missing","startedAt":"2026-01-01T00:00:00Z"}
        """);

        await using var connection = await CoordinationDaemonConnector.ConnectOrStartAsync(repo);

        Assert.NotEqual("missing", connection.Endpoint.PipeName);
        Assert.True(File.Exists(Path.Combine(metadataDirectory, "daemon.json")));
    }

    [Fact]
    public async Task ConnectorReusesValidDaemonMetadata()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");
        Directory.CreateDirectory(metadataDirectory);
        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        await using var daemon = await CoordinationDaemon.StartAsync(metadataDirectory, pipeName);

        var lease = new CoordinationDaemonLease(
            Environment.ProcessId, pipeName, repo,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var metadataPath = Path.Combine(metadataDirectory, "daemon.json");
        var tempPath = metadataPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(lease, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Move(tempPath, metadataPath, overwrite: true);

        await using var connection = await CoordinationDaemonConnector.ConnectOrStartAsync(repo);

        Assert.Equal(pipeName, connection.Endpoint.PipeName);
        var roster = await connection.Client.GetRosterAsync();
        Assert.NotNull(roster);
    }

    [Fact]
    public async Task ConnectorStartsDaemonWhenMetadataIsMissing()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");

        Assert.False(Directory.Exists(metadataDirectory));

        await using var connection = await CoordinationDaemonConnector.ConnectOrStartAsync(repo);

        Assert.True(File.Exists(Path.Combine(metadataDirectory, "daemon.json")));
        Assert.True(Directory.Exists(metadataDirectory));
        var roster = await connection.Client.GetRosterAsync();
        Assert.NotNull(roster);
    }

    [Fact]
    public async Task ConnectorUsesGitRepositoryRootWhenStartedFromSubdirectory()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var subdirectory = Path.Combine(repo, "src", "feature");
        Directory.CreateDirectory(subdirectory);

        await using var connection = await CoordinationDaemonConnector.ConnectOrStartAsync(subdirectory);

        var repoMetadataPath = Path.Combine(repo, ".pi", "coordination", "daemon.json");
        var subdirectoryMetadataPath = Path.Combine(subdirectory, ".pi", "coordination", "daemon.json");
        Assert.True(File.Exists(repoMetadataPath));
        Assert.False(File.Exists(subdirectoryMetadataPath));
        Assert.Equal(repo, connection.RepoRoot);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(repoMetadataPath));
        Assert.Equal(repo, doc.RootElement.GetProperty("repoRoot").GetString());
    }

    [Fact]
    public async Task ConnectorDisposesOwnedDaemon()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        string pipeName;

        await using (var connection = await CoordinationDaemonConnector.ConnectOrStartAsync(repo))
        {
            pipeName = connection.Endpoint.PipeName;
            Assert.NotNull(pipeName);
        }

        var client = new CoordinationClient(new CoordinationEndpoint(pipeName));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetRosterAsync());
    }

    [Fact]
    public async Task ConcurrentConnectOrStartReturnsSameEndpoint()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var tasks = Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => CoordinationDaemonConnector.ConnectOrStartAsync(repo))).ToArray();

        var connections = await Task.WhenAll(tasks);
        try
        {
            var firstPipe = connections[0].Endpoint.PipeName;
            Assert.All(connections, c => Assert.Equal(firstPipe, c.Endpoint.PipeName));
            Assert.True(File.Exists(Path.Combine(repo, ".pi", "coordination", "daemon.json")));
        }
        finally
        {
            foreach (var c in connections)
                await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReusedLeasePreservesDaemonOwnerProcessId()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");
        Directory.CreateDirectory(metadataDirectory);
        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        await using var daemon = await CoordinationDaemon.StartAsync(metadataDirectory, pipeName);

        var originalProcessId = 4242;
        var originalStartedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var originalLastChecked = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        var lease = new CoordinationDaemonLease(originalProcessId, pipeName, repo, originalStartedAt, originalLastChecked);
        var metadataPath = Path.Combine(metadataDirectory, "daemon.json");
        await WriteLeaseAtomicAsync(metadataPath, lease);

        await using var connection = await CoordinationDaemonConnector.ConnectOrStartAsync(repo);

        Assert.Equal(pipeName, connection.Endpoint.PipeName);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        var root = doc.RootElement;
        Assert.Equal(originalProcessId, root.GetProperty("processId").GetInt32());
        Assert.NotEqual(originalLastChecked, root.GetProperty("lastCheckedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ConnectorUsesInterProcessLockFile()
    {
        var repo = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var metadataDirectory = Path.Combine(repo, ".pi", "coordination");
        Directory.CreateDirectory(metadataDirectory);
        var lockPath = Path.Combine(metadataDirectory, "daemon.lock");

        await using (var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var blockedTask = Task.Run(() => CoordinationDaemonConnector.ConnectOrStartAsync(repo));
            var delayTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            var completedTask = await Task.WhenAny(blockedTask, delayTask);

            Assert.True(ReferenceEquals(completedTask, delayTask), "ConnectOrStartAsync should be blocked by lock file.");
        }

        await using var connection = await CoordinationDaemonConnector.ConnectOrStartAsync(repo);
        Assert.NotNull(connection.Endpoint.PipeName);
        Assert.False(File.Exists(lockPath), "Lock file should be cleaned up after use.");
    }

    private static async Task WriteLeaseAtomicAsync(string path, CoordinationDaemonLease lease)
    {
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(lease, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
