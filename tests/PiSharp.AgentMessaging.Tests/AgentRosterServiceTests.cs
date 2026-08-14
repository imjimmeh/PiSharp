using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class AgentRosterServiceTests
{
    [Fact]
    public void Register_AddsEntryAndPublishesChange()
    {
        var roster = new AgentRosterService();
        var changed = 0;
        roster.Changed += _ => changed++;

        roster.Register(TestAgents.Agent("root"));

        Assert.Equal(1, roster.Count);
        Assert.True(roster.TryGet("root", out var agent));
        Assert.Equal("main", agent.Role);
        Assert.Null(agent.ParentAgentId);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void UpdateStatus_ChangesStatusAndLastActive()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("root"));

        var stamp = DateTimeOffset.UtcNow.AddMinutes(1);
        var updated = roster.UpdateStatus("root", AgentStatus.Passivated, stamp);

        Assert.True(updated);
        Assert.True(roster.TryGet("root", out var agent));
        Assert.Equal(AgentStatus.Passivated, agent.Status);
        Assert.Equal(stamp, agent.LastActiveAt);
    }

    [Fact]
    public void UpdateStatus_UnknownAgent_ReturnsFalse()
    {
        var roster = new AgentRosterService();
        Assert.False(roster.UpdateStatus("nope", AgentStatus.Gone));
    }

    [Fact]
    public void Remove_RemovesEntryAndPublishesChange()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("root"));
        var changed = 0;
        roster.Changed += _ => changed++;

        var removed = roster.Remove("root");

        Assert.True(removed);
        Assert.Equal(0, roster.Count);
        Assert.False(roster.TryGet("root", out _));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void GetFamily_IncludesSelfParentSiblingsAndChildren()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("root"));
        roster.Register(TestAgents.Agent("child-a", parent: "root"));
        roster.Register(TestAgents.Agent("child-b", parent: "root"));
        roster.Register(TestAgents.Agent("grandchild", parent: "child-a"));
        roster.Register(TestAgents.Agent("unrelated"));

        var familyOfChildA = roster.GetFamily("child-a");

        var ids = familyOfChildA.Select(a => a.AgentId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("child-a", ids);       // self
        Assert.Contains("root", ids);          // parent
        Assert.Contains("child-b", ids);       // sibling
        Assert.Contains("grandchild", ids);    // child
        Assert.DoesNotContain("unrelated", ids);
    }

    [Fact]
    public void GetFamily_WithoutSelf_ExcludesSender()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("root"));
        roster.Register(TestAgents.Agent("child-a", parent: "root"));

        var family = roster.GetFamily("root", includeSelf: false);

        var ids = family.Select(a => a.AgentId).ToArray();
        Assert.Equal(["child-a"], ids);
    }

    [Fact]
    public void ResolveParent_ReturnsParentOrNull()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("root"));
        roster.Register(TestAgents.Agent("child", parent: "root"));

        Assert.Equal("root", roster.ResolveParent("child"));
        Assert.Null(roster.ResolveParent("root"));
        Assert.Null(roster.ResolveParent("unknown"));
    }

    [Fact]
    public void GetRoster_ReturnsSnapshotOrderedByCreation()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("b"));
        roster.Register(TestAgents.Agent("a"));

        var agents = roster.GetRoster();
        Assert.Equal(["b", "a"], agents.Select(a => a.AgentId).ToArray());
    }
}
