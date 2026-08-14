using Xunit;

namespace PiSharp.AgentMessaging.Tests;

internal static class TestAgents
{
    public static AgentInfo Agent(
        string id,
        string? parent = null,
        string? role = null,
        AgentStatus status = AgentStatus.Running)
        => new(
            id,
            Name: null,
            Role: role ?? (parent is null ? "main" : "subagent"),
            ParentAgentId: parent,
            Status: status,
            Cwd: "/",
            Model: null,
            ThinkingLevel: null,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow);
}
