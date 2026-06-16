using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class CoordinationDaemonTests
{
    [Fact]
    public async Task ClientRegistersAgentAndReadsRosterFromDaemon()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-1", Environment.ProcessId, "session-1", null, "/repo"));
        var roster = await client.GetRosterAsync();

        Assert.Contains(roster.Agents, agent => agent.AgentId == "agent-1");
    }

    [Fact]
    public async Task DaemonReplaysPersistedRecordsIntoRoster()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);
        await store.AppendAsync(new AgentRegisteredRecord("agent-persisted", 42, "s-p", null, "/repo-p", DateTimeOffset.UnixEpoch));

        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);
        var roster = await client.GetRosterAsync();

        Assert.Contains(roster.Agents, agent => agent.AgentId == "agent-persisted"
                                               && agent.ProcessId == 42
                                               && agent.SessionId == "s-p");
    }

    [Fact]
    public async Task ClientSendsMessageAndInboxRetrievesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var clientA = new CoordinationClient(daemon.Endpoint);
        var clientB = new CoordinationClient(daemon.Endpoint);

        await clientA.RegisterAgentAsync(new AgentRegistration("agent-a", 1, null, null, "/"));
        await clientB.RegisterAgentAsync(new AgentRegistration("agent-b", 2, null, null, "/"));
        await clientA.SendMessageAsync("msg-1", "agent-a", "agent-b", "hello from A");

        var inbox = await clientB.GetInboxAsync("agent-b");
        Assert.Contains(inbox.Messages, m => m.MessageId == "msg-1" && m.Body == "hello from A");
    }

    [Fact]
    public async Task BroadcastMessageIsDeliveredToAllAgents()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("sender", 1, null, null, "/"));
        await client.RegisterAgentAsync(new AgentRegistration("receiver", 2, null, null, "/"));
        await client.SendMessageAsync("msg-broadcast", "sender", "all", "broadcast message");

        var inbox = await client.GetInboxAsync("receiver");
        Assert.Contains(inbox.Messages, m => m.MessageId == "msg-broadcast");
    }

    [Fact]
    public async Task ClientRecordsFileReadAndWriteAndDaemonPersists()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-f", 1, null, null, "/"));
        await client.RecordFileReadAsync("agent-f", "README.md");
        await client.RecordFileWriteAsync("agent-f", "README.md");

        var store = new CoordinationJsonlStore(directory);
        var records = await store.ReadAllAsync();
        Assert.Contains(records, r => r is FileReadRecord { AgentId: "agent-f", Path: "README.md" });
        Assert.Contains(records, r => r is FileWriteRecord { AgentId: "agent-f", Path: "README.md" });
    }

    [Fact]
    public async Task FileReadNormalizesAbsolutePathToRepoRelativeUsingAgentCwd()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var cwd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\repo" : "/repo";
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-abs", 1, null, null, cwd));
        var absolutePath = cwd + "/src/file.cs";
        await client.RecordFileReadAsync("agent-abs", absolutePath);

        var store = new CoordinationJsonlStore(directory);
        var records = await store.ReadAllAsync();
        Assert.Contains(records, r => r is FileReadRecord { AgentId: "agent-abs", Path: "src/file.cs" });
    }

    [Fact]
    public async Task FileWriteNormalizesAbsolutePathToRepoRelativeUsingAgentCwd()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var cwd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\repo" : "/repo";
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-abs-write", 1, null, null, cwd));
        var absolutePath = cwd + "/src/file.cs";
        await client.RecordFileWriteAsync("agent-abs-write", absolutePath);

        var store = new CoordinationJsonlStore(directory);
        var records = await store.ReadAllAsync();
        Assert.Contains(records, r => r is FileWriteRecord { AgentId: "agent-abs-write", Path: "src/file.cs" });
    }

    [Fact]
    public async Task PreflightNormalizesAbsolutePathAgainstStoredRepoRelativePaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var cwd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\repo" : "/repo";
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-a", 1, null, null, cwd));
        await client.RegisterAgentAsync(new AgentRegistration("agent-b", 2, null, null, cwd));

        await client.RecordFileReadAsync("agent-a", "src/lib/helper.cs");
        await client.RecordFileWriteAsync("agent-b", "src/lib/helper.cs");

        var preflight = await client.PreflightToolAsync(
            "agent-a",
            "write",
            JsonDocument.Parse($$"""{"filePath":"{{cwd.Replace(@"\", "/")}}/src/lib/helper.cs"}""").RootElement);

        Assert.True(preflight.ShouldWarn);
        Assert.Contains("agent-b", preflight.Message);
    }

    [Fact]
    public async Task BlankAgentIdReturnsErrorResponse()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendHeartbeatAsync(""));

        Assert.Contains("error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeartbeatUpdatesAgentLastSeen()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-hb", 1, null, null, "/"));
        await client.SendHeartbeatAsync("agent-hb");

        var store = new CoordinationJsonlStore(directory);
        var records = await store.ReadAllAsync();
        Assert.Contains(records, r => r is AgentHeartbeatRecord { AgentId: "agent-hb" });
    }

    [Fact]
    public async Task GetBriefReturnsContentWhenAgentsExist()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-brief", 1, null, null, "/"));

        var brief = await client.GetBriefAsync("agent-brief");
        Assert.Contains("agent(s) active", brief.Content);
    }

    [Fact]
    public async Task MalformedJsonRequestReturnsErrorResponse()
    {
        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName);

        var response = await SendRawAndReceiveAsync(pipeName, "not valid json");

        AssertOkIsFalse(response);
        Assert.Contains("Malformed", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyRequestLineReturnsErrorResponse()
    {
        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName);

        var response = await SendRawAndReceiveAsync(pipeName, "");

        AssertOkIsFalse(response);
        Assert.Contains("empty", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownRequestTypeReturnsErrorResponse()
    {
        var pipeName = $"pisharp-coordination-{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName);

        var response = await SendRawAndReceiveAsync(pipeName, """{"type":"bogus_command"}""");

        AssertOkIsFalse(response);
        Assert.Contains("Unknown", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetInboxRespectsSinceTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("sender", 1, null, null, "/"));
        await client.RegisterAgentAsync(new AgentRegistration("receiver", 2, null, null, "/"));

        var beforeCutoff = DateTimeOffset.UtcNow;
        await client.SendMessageAsync("msg-old", "sender", "receiver", "old message");

        var afterCutoff = DateTimeOffset.UtcNow;
        await client.SendMessageAsync("msg-new", "sender", "receiver", "new message");

        var inboxAll = await client.GetInboxAsync("receiver");
        Assert.Equal(2, inboxAll.Messages.Length);

        var inboxFiltered = await client.GetInboxAsync("receiver", sinceTimestamp: afterCutoff);
        Assert.Single(inboxFiltered.Messages);
        Assert.Equal("msg-new", inboxFiltered.Messages[0].MessageId);
        Assert.DoesNotContain(inboxFiltered.Messages, m => m.MessageId == "msg-old");
    }

    [Fact]
    public async Task GetInboxWithCancelledTokenThrows()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-x", 1, null, null, "/"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetInboxAsync("agent-x", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ClientRecordsSubagentEventAndDaemonPersists()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-sa", 1, null, null, "/repo"));

        var record = new SubagentObservedRecord(
            "sub-1", "Explore", "inspect files", "completed",
            "subagents:completed", 1500.0, 12, 500, 1200,
            "parent-session", "/repo", DateTimeOffset.UnixEpoch);

        await client.RecordSubagentEventAsync(record);

        var store = new CoordinationJsonlStore(directory);
        var records = await store.ReadAllAsync();
        Assert.Contains(records, r => r is SubagentObservedRecord {
            SubagentId: "sub-1",
            SubagentType: "Explore",
            EventName: "subagents:completed",
            DurationMs: 1500.0,
            ToolUses: 12,
            InputTokens: 500,
            OutputTokens: 1200
        });
    }

    [Fact]
    public async Task DaemonReplaysPersistedSubagentEvent()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);
        await store.AppendAsync(new SubagentObservedRecord(
            "sub-persist", "Search", "find docs", "running",
            "subagents:started", null, null, null, null,
            null, "/repo-p", DateTimeOffset.UnixEpoch));

        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");

        var replayedRecords = daemon.State.SubagentEvents.ToArray();
        Assert.Contains(replayedRecords, r => r.SubagentId == "sub-persist"
                                              && r.SubagentType == "Search"
                                              && r.Description == "find docs"
                                              && r.Cwd == "/repo-p");
    }

    [Fact]
    public async Task RecordSubagentEventWithCancelledTokenThrows()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-ct", 1, null, null, "/"));

        var record = new SubagentObservedRecord(
            "sub-1", null, null, null,
            "subagents:started", null, null, null, null,
            null, "/repo", DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RecordSubagentEventAsync(record, cts.Token));
    }

    [Fact]
    public async Task DaemonRejectsSubagentEventWithUnknownEventName()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-unk", 1, null, null, "/"));

        var record = new SubagentObservedRecord(
            "sub-x", null, null, null,
            "subagents:unknown", null, null, null, null,
            null, "/repo", DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RecordSubagentEventAsync(record));
        Assert.Contains("Unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DaemonTruncatesOversizedTypeDescriptionAndStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-size", 1, null, null, "/"));

        var longType = new string('t', 500);
        var longDesc = new string('d', 2000);
        var longStatus = new string('s', 200);

        var record = new SubagentObservedRecord(
            "sub-size", longType, longDesc, longStatus,
            "subagents:started", null, null, null, null,
            null, "/repo", DateTimeOffset.UtcNow);

        await client.RecordSubagentEventAsync(record);

        var store = new CoordinationJsonlStore(directory);
        var records = await store.ReadAllAsync();
        var persisted = Assert.Single(records.OfType<SubagentObservedRecord>());
        Assert.NotNull(persisted.SubagentType);
        Assert.True(persisted.SubagentType!.Length <= 256);
        Assert.NotNull(persisted.Description);
        Assert.True(persisted.Description!.Length <= 1024);
        Assert.NotNull(persisted.Status);
        Assert.True(persisted.Status!.Length <= 64);
    }

    [Fact]
    public async Task DaemonRejectsSubagentEventWithBlankEventName()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-blank", 1, null, null, "/"));

        var record = new SubagentObservedRecord(
            "sub-b", null, null, null,
            "   ", null, null, null, null,
            null, "/repo", DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RecordSubagentEventAsync(record));
        Assert.Contains("Unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientUnregistersAgentAndRosterExcludesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var daemon = await CoordinationDaemon.StartAsync(directory, pipeName: $"pisharp-coordination-{Guid.NewGuid():N}");
        var client = new CoordinationClient(daemon.Endpoint);

        await client.RegisterAgentAsync(new AgentRegistration("agent-gone", Environment.ProcessId, "session-1", null, "/repo"));
        var rosterBefore = await client.GetRosterAsync();
        Assert.Contains(rosterBefore.Agents, a => a.AgentId == "agent-gone");

        await client.UnregisterAgentAsync("agent-gone");
        var rosterAfter = await client.GetRosterAsync();
        Assert.DoesNotContain(rosterAfter.Agents, a => a.AgentId == "agent-gone");
    }

    [Fact]
    public async Task UnregisteredAgentIsExcludedAfterDaemonRestartFromPersistedEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var pipeName1 = $"pisharp-coordination-{Guid.NewGuid():N}";

        await using (var daemon1 = await CoordinationDaemon.StartAsync(directory, pipeName1))
        {
            var client = new CoordinationClient(daemon1.Endpoint);
            await client.RegisterAgentAsync(new AgentRegistration("agent-restarted", Environment.ProcessId, "session-1", null, "/repo"));
            await client.UnregisterAgentAsync("agent-restarted");
        }

        var pipeName2 = $"pisharp-coordination-{Guid.NewGuid():N}";
        await using var daemon2 = await CoordinationDaemon.StartAsync(directory, pipeName2);
        var client2 = new CoordinationClient(daemon2.Endpoint);
        var roster = await client2.GetRosterAsync();

        Assert.DoesNotContain(roster.Agents, a => a.AgentId == "agent-restarted");
    }

    private static async Task<string> SendRawAndReceiveAsync(string pipeName, string rawLine)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await client.ConnectAsync(5000);

        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(rawLine);

        var response = await reader.ReadLineAsync();
        if (response is null)
            throw new InvalidOperationException("Daemon closed connection without response.");

        return response;
    }

    private static void AssertOkIsFalse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
            $"Expected ok:false but got: {responseJson}");
    }
}
