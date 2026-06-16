using PiSharp.Coordination;
using System.Text.Json;
using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class PiSubagentsEventAdapterTests
{
    [Fact]
    public void AdapterMapsSubagentsStartedEventToAgentRegistration()
    {
        using var payload = JsonDocument.Parse("""
        {"id":"sub-1","type":"Explore","description":"inspect files","status":"running"}
        """);

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: "parent-1", cwd: "/repo");

        var started = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal("sub-1", started.SubagentId);
        Assert.Equal("Explore", started.SubagentType);
        Assert.Equal("inspect files", started.Description);
        Assert.Equal("running", started.Status);
        Assert.Equal("subagents:started", started.EventName);
        Assert.Equal("parent-1", started.ParentSessionId);
        Assert.Equal("/repo", started.Cwd);
        Assert.Equal(default, started.DurationMs);
        Assert.Equal(default, started.ToolUses);
        Assert.Equal(default, started.InputTokens);
        Assert.Equal(default, started.OutputTokens);
    }

    [Fact]
    public void AdapterMapsSubagentsCreatedEvent()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-2","type":"Search"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:created", payload.RootElement, parentSessionId: null, cwd: "/tmp");

        var created = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal("sub-2", created.SubagentId);
        Assert.Equal("Search", created.SubagentType);
        Assert.Equal("subagents:created", created.EventName);
    }

    [Fact]
    public void AdapterMapsSubagentsCompletedEvent()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-3","type":"Explore","status":"completed","durationMs":1500.5,"toolUses":12,"inputTokens":500,"outputTokens":1200}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:completed", payload.RootElement, parentSessionId: null, cwd: "");

        var completed = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal("sub-3", completed.SubagentId);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(1500.5, completed.DurationMs);
        Assert.Equal(12, completed.ToolUses);
        Assert.Equal(500, completed.InputTokens);
        Assert.Equal(1200, completed.OutputTokens);
    }

    [Fact]
    public void AdapterMapsSubagentsFailedEvent()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-4","type":"Execute","status":"failed","durationMs":300}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:failed", payload.RootElement, parentSessionId: null, cwd: "");

        var failed = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal("sub-4", failed.SubagentId);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(300, failed.DurationMs);
        Assert.Equal("subagents:failed", failed.EventName);
    }

    [Fact]
    public void AdapterMapsSubagentsSteeredEvent()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-5","type":"Explore","status":"steered"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:steered", payload.RootElement, parentSessionId: null, cwd: "");

        var steered = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal("sub-5", steered.SubagentId);
        Assert.Equal("steered", steered.Status);
    }

    [Fact]
    public void AdapterMapsSubagentsCompactedEvent()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-6","type":"Explore","status":"compacted"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:compacted", payload.RootElement, parentSessionId: null, cwd: "");

        var compacted = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal("sub-6", compacted.SubagentId);
        Assert.Equal("compacted", compacted.Status);
    }

    [Fact]
    public void AdapterReturnsNullForUnknownEventName()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-x"}""");

        var record = PiSubagentsEventAdapter.TryMap("tool_call", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForNonMatchingPrefix()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-x"}""");

        var record = PiSubagentsEventAdapter.TryMap("agent_start", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForUnlistedSubagentsEventName()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-x","type":"Test"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:unknown", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForNonObjectPayloadArray()
    {
        using var payload = JsonDocument.Parse("""[1,2,3]""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForNonObjectPayloadNumber()
    {
        using var payload = JsonDocument.Parse("42");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForNonObjectPayloadString()
    {
        using var payload = JsonDocument.Parse("\"just a string\"");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterTruncatesLongTypeField()
    {
        var longType = new string('x', 300);
        using var payload = JsonDocument.Parse($$"""{"id":"sub-t","type":"{{longType}}"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        var started = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal(256, started.SubagentType!.Length);
    }

    [Fact]
    public void AdapterTruncatesLongDescriptionField()
    {
        var longDesc = new string('y', 2000);
        using var payload = JsonDocument.Parse($$"""{"id":"sub-d","description":"{{longDesc}}"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        var started = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal(1024, started.Description!.Length);
    }

    [Fact]
    public void AdapterTruncatesLongStatusField()
    {
        var longStatus = new string('z', 200);
        using var payload = JsonDocument.Parse($$"""{"id":"sub-s","status":"{{longStatus}}"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        var started = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal(64, started.Status!.Length);
    }

    [Fact]
    public void AdapterAllowsTypeDescriptionStatusWithinLimits()
    {
        var type = new string('t', 256);
        var desc = new string('d', 1024);
        var status = new string('s', 64);
        using var payload = JsonDocument.Parse($$"""{"id":"sub-limits","type":"{{type}}","description":"{{desc}}","status":"{{status}}"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        var started = Assert.IsType<SubagentObservedRecord>(record);
        Assert.Equal(type, started.SubagentType);
        Assert.Equal(desc, started.Description);
        Assert.Equal(status, started.Status);
    }

    [Fact]
    public void AdapterObjectOverloadAlsoUsesAllowlist()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-x"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:unknown", new object(), parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForMissingId()
    {
        using var payload = JsonDocument.Parse("""{"type":"Explore"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForWhitespaceId()
    {
        using var payload = JsonDocument.Parse("""{"id":"   ","type":"Explore"}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForNonStringId()
    {
        using var payload = JsonDocument.Parse("""{"id":123}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterReturnsNullForNullId()
    {
        using var payload = JsonDocument.Parse("""{"id":null}""");

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        Assert.Null(record);
    }

    [Fact]
    public void AdapterSetsTimestampToUtcNow()
    {
        using var payload = JsonDocument.Parse("""{"id":"sub-ts"}""");
        var before = DateTimeOffset.UtcNow;

        var record = PiSubagentsEventAdapter.TryMap("subagents:started", payload.RootElement, parentSessionId: null, cwd: "");

        var after = DateTimeOffset.UtcNow;
        var observed = Assert.IsType<SubagentObservedRecord>(record);
        Assert.InRange(observed.Timestamp, before, after);
    }

    [Fact]
    public void AllMappedEventNamesProduceValidRecord()
    {
        var eventNames = new[] { "subagents:created", "subagents:started", "subagents:completed", "subagents:failed", "subagents:steered", "subagents:compacted" };

        foreach (var eventName in eventNames)
        {
            using var payload = JsonDocument.Parse($$"""{"id":"agent-{{eventName}}","type":"Test"}""");
            var record = PiSubagentsEventAdapter.TryMap(eventName, payload.RootElement, parentSessionId: null, cwd: "");
            Assert.NotNull(record);
            Assert.Equal(eventName, record!.EventName);
        }
    }
}
