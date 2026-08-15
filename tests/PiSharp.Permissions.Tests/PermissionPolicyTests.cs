using PiSharp.Permissions;
using Xunit;

namespace PiSharp.Permissions.Tests;

public sealed class PermissionPolicyTests
{
    private static PermissionsPolicy Policy(
        string mode = PermissionsPolicy.ModePrompt,
        IReadOnlyList<PermissionRule>? allow = null,
        IReadOnlyList<PermissionRule>? deny = null,
        IReadOnlyList<PermissionRule>? ask = null,
        bool headlessDeny = true)
        => new(mode, allow ?? [], deny ?? [], ask ?? [], headlessDeny);

    private static string Args(string command) => $"{{\"command\":\"{command}\"}}";

    // --- Precedence: deny > ask > allow ---

    [Fact]
    public void OverlappingRules_MostRestrictiveWins_DenyOverAllow()
    {
        var policy = Policy(
            allow: [new PermissionRule("bash")],
            deny: [new PermissionRule("bash", "git push.*")]);

        var decision = policy.Evaluate("bash", Args("git push origin main"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Deny, decision.Action);
        Assert.Contains("git push", decision.Reason);
        Assert.Contains("rule:bash", decision.MatchedRule ?? string.Empty);
    }

    [Fact]
    public void OverlappingRules_MostRestrictiveWins_DenyOverAsk()
    {
        var policy = Policy(
            ask: [new PermissionRule("bash")],
            deny: [new PermissionRule("bash", "git push.*")]);

        var decision = policy.Evaluate("bash", Args("git push origin main"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Deny, decision.Action);
    }

    [Fact]
    public void OverlappingRules_MostRestrictiveWins_AskOverAllow()
    {
        var policy = Policy(
            allow: [new PermissionRule("bash")],
            ask: [new PermissionRule("bash", "git push.*")]);

        var decision = policy.Evaluate("bash", Args("git push origin main"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Ask, decision.Action);
    }

    [Fact]
    public void AllowRule_AllowsMatchingTool()
    {
        var policy = Policy(allow: [new PermissionRule("bash")]);

        var decision = policy.Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    // --- Pattern matching over serialized args ---

    [Fact]
    public void Pattern_MatchesSerializedArgs_CaseInsensitively()
    {
        var policy = Policy(deny: [new PermissionRule("bash", "git push.*")]);

        var decision = policy.Evaluate("bash", Args("GIT PUSH origin main"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Deny, decision.Action);
    }

    [Fact]
    public void Pattern_NonMatchingArgs_DoesNotApply()
    {
        var policy = Policy(deny: [new PermissionRule("bash", "git push.*")]);

        var decision = policy.Evaluate("bash", Args("git status"), DangerousOpDetector.Bash, headless: false);

        Assert.NotEqual(PermissionAction.Deny, decision.Action);
    }

    [Fact]
    public void Rule_OnlyAppliesToNamedTool()
    {
        var policy = Policy(deny: [new PermissionRule("bash", "git push.*")]);

        var decision = policy.Evaluate("write", "{\"path\":\"a.txt\"}", DangerousOpDetector.None, headless: false);

        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    // --- Dangerous defaults when no rule matches ---

    [Fact]
    public void NoMatch_BashDefaultsToAsk()
    {
        var decision = Policy().Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Ask, decision.Action);
        Assert.Contains("dangerous-default", decision.MatchedRule ?? string.Empty);
    }

    [Fact]
    public void NoMatch_WriteOutsideCwdDefaultsToDeny()
    {
        var decision = Policy().Evaluate("write", "{\"path\":\"../outside.txt\"}", DangerousOpDetector.WriteOutsideCwd, headless: false);

        Assert.Equal(PermissionAction.Deny, decision.Action);
    }

    [Fact]
    public void NoMatch_WriteOverwriteDefaultsToAsk()
    {
        var decision = Policy().Evaluate("write", "{\"path\":\"existing.txt\"}", DangerousOpDetector.WriteOverwrite, headless: false);

        Assert.Equal(PermissionAction.Ask, decision.Action);
    }

    [Fact]
    public void NoMatch_ReadOnlyToolDefaultsToAllow()
    {
        var decision = Policy().Evaluate("read", "{\"path\":\"a.txt\"}", DangerousOpDetector.None, headless: false);

        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    [Fact]
    public void NoMatch_UnlistedExtensionToolWithoutDangerousCategory_StillAllows()
    {
        // DangerousOpDetector now classifies unlisted tools as Unknown, so the None
        // category (pass-through) still maps to Allow at the policy level.
        var decision = Policy().Evaluate("my-extension-tool", "{}", DangerousOpDetector.None, headless: false);
        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    [Fact]
    public void UnknownCategory_PromptMode_DefaultsToAsk()
    {
        var decision = Policy().Evaluate("my-custom-tool", "{}", DangerousOpDetector.Unknown, headless: false);
        Assert.Equal(PermissionAction.Ask, decision.Action);
        Assert.Contains("approval required", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownCategory_StrictMode_Denies()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeStrict, allow: [new PermissionRule("read")]);
        var decision = policy.Evaluate("my-custom-tool", "{}", DangerousOpDetector.Unknown, headless: false);
        Assert.Equal(PermissionAction.Deny, decision.Action);
        Assert.Contains("strict", decision.Reason);
    }

    [Fact]
    public void UnknownCategory_AutomaticMode_Allows()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeAutomatic);
        var decision = policy.Evaluate("my-custom-tool", "{}", DangerousOpDetector.Unknown, headless: false);
        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    [Fact]
    public void McpSpawnCategory_PromptMode_Asks()
    {
        var decision = Policy().Evaluate("mcp.fileserver.exec", "{\"command\":\"run.sh\"}", DangerousOpDetector.McpSpawn, headless: false);
        Assert.Equal(PermissionAction.Ask, decision.Action);
        Assert.Contains("command", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void McpSpawnCategory_StrictMode_Denies()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeStrict, allow: [new PermissionRule("read")]);
        var decision = policy.Evaluate("mcp.fileserver.exec", "{\"command\":\"run.sh\"}", DangerousOpDetector.McpSpawn, headless: false);
        Assert.Equal(PermissionAction.Deny, decision.Action);
    }

    [Fact]
    public void McpSpawnCategory_AutomaticMode_Allows()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeAutomatic);
        var decision = policy.Evaluate("mcp.fileserver.exec", "{\"command\":\"run.sh\"}", DangerousOpDetector.McpSpawn, headless: false);
        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    [Fact]
    public void RmRfCategory_PromptMode_AsksWithRmReason()
    {
        var decision = Policy().Evaluate("bash", "{\"command\":\"rm -rf /tmp\"}", DangerousOpDetector.RmRf, headless: false);
        Assert.Equal(PermissionAction.Ask, decision.Action);
        Assert.Contains("rm", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictMode_DeniesUnlistedMcpTool()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeStrict, allow: [new PermissionRule("read")]);
        var decision = policy.Evaluate("mcp.foo.read", "{}", DangerousOpDetector.None, headless: false);
        Assert.Equal(PermissionAction.Deny, decision.Action);
        Assert.Contains("strict", decision.Reason);
    }

    // --- Mode postures ---

    [Fact]
    public void StrictMode_DeniesUnlistedTool()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeStrict, allow: [new PermissionRule("read")]);

        var decision = policy.Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Deny, decision.Action);
        Assert.Contains("strict", decision.Reason);
    }

    [Fact]
    public void StrictMode_AllowsExplicitlyListedTool()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeStrict, allow: [new PermissionRule("read")]);

        var decision = policy.Evaluate("read", "{\"path\":\"a.txt\"}", DangerousOpDetector.None, headless: false);

        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    [Fact]
    public void StrictMode_StillEnforcesAskRules()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeStrict, ask: [new PermissionRule("bash")]);

        var decision = policy.Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Ask, decision.Action);
    }

    [Fact]
    public void AutomaticMode_ResolvesAskToAllow()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeAutomatic);

        var decision = policy.Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Allow, decision.Action);
        Assert.Contains("Automatic", decision.Reason);
    }

    [Fact]
    public void AutomaticMode_KeepsDenyEnforced()
    {
        var policy = Policy(mode: PermissionsPolicy.ModeAutomatic, deny: [new PermissionRule("bash")]);

        var decision = policy.Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Deny, decision.Action);
    }

    [Fact]
    public void Headless_ResolvesAskToDeny_WhenHeadlessDenyOn()
    {
        var decision = Policy().Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: true);

        Assert.Equal(PermissionAction.Deny, decision.Action);
        Assert.Contains("headless", decision.Reason);
    }

    [Fact]
    public void Headless_ResolvesAskToAllow_WhenHeadlessDenyOff()
    {
        var policy = Policy(headlessDeny: false);

        var decision = policy.Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: true);

        Assert.Equal(PermissionAction.Allow, decision.Action);
    }

    [Fact]
    public void Interactive_AskStaysAsk()
    {
        var decision = Policy().Evaluate("bash", Args("echo hello"), DangerousOpDetector.Bash, headless: false);

        Assert.Equal(PermissionAction.Ask, decision.Action);
    }

    // --- Malformed rules fail loud ---

    [Fact]
    public void InvalidPattern_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => Policy(deny: [new PermissionRule("bash", "([invalid")]));
    }

    [Fact]
    public void DefaultPolicy_HasDocumentedDefaults()
    {
        var policy = PermissionsPolicy.Default;

        Assert.Equal(PermissionsPolicy.ModePrompt, policy.Mode);
        Assert.True(policy.HeadlessDeny);
        Assert.Equal(3600, policy.GrantTtlSeconds);
        Assert.True(policy.Audit);
        Assert.Empty(policy.AllowRules);
        Assert.Empty(policy.DenyRules);
        Assert.Empty(policy.AskRules);
    }
}
