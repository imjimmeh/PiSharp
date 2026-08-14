using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class AgentMessagingBriefFormatterTests
{
    [Fact]
    public void FormatBrief_Empty_ReturnsNull()
    {
        Assert.Null(AgentMessagingBriefFormatter.FormatBrief());
        Assert.Null(AgentMessagingBriefFormatter.FormatBrief(family: [], messages: []));
    }

    [Fact]
    public void FormatBrief_Family_RendersTableLines()
    {
        var family = new[]
        {
            new AgentInfo("root", null, "main", null, AgentStatus.Running, "/", null, null, DateTimeOffset.UtcNow, null),
            new AgentInfo("child", "helper", "subagent", "root", AgentStatus.Running, "/", null, null, DateTimeOffset.UtcNow, null),
        };

        var brief = AgentMessagingBriefFormatter.FormatBrief(family: family);

        Assert.NotNull(brief);
        Assert.Contains("## Messaging Brief", brief);
        Assert.Contains("### Family Agents", brief);
        Assert.Contains("`root` — main (running)", brief);
        Assert.Contains("`child` (helper) — subagent (running) → parent `root`", brief);
    }

    [Fact]
    public void FormatBrief_Messages_RendersUnreadSection()
    {
        var message = new AgentMessage("m1", "child-a", ["root"], "ping", AgentMessageDelivery.Steer, DateTimeOffset.UtcNow, AgentMessageStatus.Delivered);

        var brief = AgentMessagingBriefFormatter.FormatBrief(messages: [message]);

        Assert.NotNull(brief);
        Assert.Contains("### Unread Agent Messages", brief);
        Assert.Contains("**child-a** (steer): ping", brief);
    }

    [Fact]
    public void FormatBrief_FamilyAndMessages_CombinesSections()
    {
        var family = new[] { new AgentInfo("root", null, "main", null, AgentStatus.Running, "/", null, null, DateTimeOffset.UtcNow, null) };
        var message = new AgentMessage("m1", "child-a", ["root"], "hi", AgentMessageDelivery.NextTurn, DateTimeOffset.UtcNow, AgentMessageStatus.Delivered);

        var brief = AgentMessagingBriefFormatter.FormatBrief(family, [message]);

        Assert.NotNull(brief);
        Assert.Contains("### Family Agents", brief);
        Assert.Contains("### Unread Agent Messages", brief);
    }
}
