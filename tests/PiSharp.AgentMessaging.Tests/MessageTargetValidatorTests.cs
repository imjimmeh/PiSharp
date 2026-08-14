using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class MessageTargetValidatorTests
{
    private static readonly string[] DefaultWhitelist = ["main", "subagent"];

    private static AgentRosterService BuildFamily()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("root"));
        roster.Register(TestAgents.Agent("child-a", parent: "root"));
        roster.Register(TestAgents.Agent("child-b", parent: "root"));
        roster.Register(TestAgents.Agent("grandchild", parent: "child-a"));
        return roster;
    }

    [Fact]
    public void DirectFamilyTarget_IsValid()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("child-a", roster, ["child-b"], DefaultWhitelist);

        Assert.True(result.Valid);
        Assert.Equal(["child-b"], result.Recipients);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void ParentTarget_ResolvesToParent()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("child-a", roster, ["parent"], DefaultWhitelist);

        Assert.True(result.Valid);
        Assert.Equal(["root"], result.Recipients);
    }

    [Fact]
    public void ParentTarget_WithNoParent_IsInvalid()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("root", roster, ["parent"], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
    }

    [Fact]
    public void AllTarget_ResolvesFamilyMinusSelf()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("child-a", roster, ["all"], DefaultWhitelist);

        Assert.True(result.Valid);
        var ids = result.Recipients.ToHashSet(StringComparer.Ordinal);
        Assert.Contains("root", ids);
        Assert.Contains("child-b", ids);
        Assert.Contains("grandchild", ids);
        Assert.DoesNotContain("child-a", ids);
    }

    [Fact]
    public void AllTarget_WithEmptyFamily_IsInvalid()
    {
        var roster = new AgentRosterService();
        roster.Register(TestAgents.Agent("lonely"));

        var result = MessageTargetValidator.Validate("lonely", roster, ["all"], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
    }

    [Fact]
    public void UnknownTarget_IsInvalid()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("child-a", roster, ["nobody"], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
        Assert.Contains("unknown target", result.ErrorMessage);
    }

    [Fact]
    public void OutOfFamilyTarget_IsInvalid()
    {
        var roster = BuildFamily();
        roster.Register(TestAgents.Agent("other-root"));
        var result = MessageTargetValidator.Validate("child-a", roster, ["other-root"], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
        Assert.Contains("outside the sender's family", result.ErrorMessage);
    }

    [Fact]
    public void SelfTarget_IsInvalid()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("child-a", roster, ["child-a"], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
        Assert.Contains("self-targeting", result.ErrorMessage);
    }

    [Fact]
    public void RoleNotWhitelisted_IsInvalid()
    {
        var roster = BuildFamily();
        roster.Register(TestAgents.Agent("special", parent: "root", role: "observer"));

        var result = MessageTargetValidator.Validate("child-a", roster, ["special"], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
        Assert.Contains("not whitelisted", result.ErrorMessage);
    }

    [Fact]
    public void EmptyWhitelist_AllowsAnyRole()
    {
        var roster = BuildFamily();
        roster.Register(TestAgents.Agent("special", parent: "root", role: "observer"));

        var result = MessageTargetValidator.Validate("child-a", roster, ["special"], []);

        Assert.True(result.Valid);
        Assert.Equal(["special"], result.Recipients);
    }

    [Fact]
    public void EmptyTargets_IsInvalid()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("child-a", roster, [], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
    }

    [Fact]
    public void SenderNotInRoster_IsInvalid()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("ghost", roster, ["root"], DefaultWhitelist);

        Assert.False(result.Valid);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
    }

    [Fact]
    public void DuplicateTargets_AreDeduplicated()
    {
        var roster = BuildFamily();
        var result = MessageTargetValidator.Validate("child-a", roster, ["child-b", "child-b"], DefaultWhitelist);

        Assert.True(result.Valid);
        Assert.Equal(["child-b"], result.Recipients);
    }
}
