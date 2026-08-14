using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Acp.Tests;


/// <summary>
/// Permission gate policy matrix (plan §4.2 / §9 / §10): yolo allows, read-only blocks
/// non-allowlisted, ask blocks-until-responded, and the allow/reject/cancelled outcomes drive
/// whether the tool call proceeds. Exercises <see cref="AcpPermissionGate.Create"/> directly
/// over a constructed <see cref="ExtensionMiddlewareContext"/>.
/// </summary>
public sealed class AcpPermissionGateTests
{
    private static ExtensionMiddlewareContext NewContext(string toolName)
    {
        var args = JsonDocument.Parse("""{"a":1}""").RootElement.Clone();
        var tool = new ToolCallContent("tc-1", toolName, args);
        var assistant = new AssistantMessage([tool], StopReason: "tool_use");
        var before = new BeforeToolCallContext(assistant, tool, args, new AgentContext("sys", [assistant], null));
        var extEvent = new ExtensionEvent(
            "before_tool_call",
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ToolCall(tool.Id, tool.Name, new Dictionary<string, object?>())),
            null);
        return new ExtensionMiddlewareContext(extEvent, before);
    }

    private static async Task<(bool blocked, string? reason, bool nextCalled)> Run(
        AcpApprovalMode mode,
        string toolName,
        IAcpPermissionResponder? responder = null,
        IReadOnlyList<string>? allowlist = null)
    {
        var gate = AcpPermissionGate.Create(new AcpPermissionGateOptions(mode, allowlist, responder));
        var ctx = NewContext(toolName);
        var nextCalled = false;
        await gate(ctx, (_, _) => { nextCalled = true; return Task.CompletedTask; }, default);
        return (ctx.Blocked, ctx.BlockReason, nextCalled);
    }

    [Fact]
    public async Task Yolo_AllowsAllTools()
    {
        var (blocked, reason, nextCalled) = await Run(AcpApprovalMode.Yolo, "bash");
        Assert.False(blocked);
        Assert.Null(reason);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ReadOnly_AllowlistedTool_Passes()
    {
        var (blocked, _, nextCalled) = await Run(AcpApprovalMode.ReadOnly, "read");
        Assert.False(blocked);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ReadOnly_NonAllowlistedTool_BlockedWithoutAsking()
    {
        var responder = new ThrowingResponder(); // would throw if asked
        var (blocked, reason, nextCalled) = await Run(AcpApprovalMode.ReadOnly, "bash", responder);
        Assert.True(blocked);
        Assert.Equal("read-only approval mode", reason);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Ask_AllowlistedTool_PassesWithoutRoundTrip()
    {
        var responder = new ThrowingResponder(); // would throw if asked
        var (blocked, _, nextCalled) = await Run(AcpApprovalMode.Ask, "read", responder);
        Assert.False(blocked);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Ask_AllowOnce_Proceeds()
    {
        var responder = new FakeResponder(new AcpPermissionOutcome("selected", "allow-once"));
        var (blocked, reason, nextCalled) = await Run(AcpApprovalMode.Ask, "bash", responder);
        Assert.False(blocked);
        Assert.Null(reason);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Ask_RejectOnce_Blocked()
    {
        var responder = new FakeResponder(new AcpPermissionOutcome("selected", "reject-once"));
        var (blocked, reason, nextCalled) = await Run(AcpApprovalMode.Ask, "bash", responder);
        Assert.True(blocked);
        Assert.Equal("rejected by user", reason);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Ask_CancelledOutcome_BlockedAsCancelled()
    {
        var responder = new FakeResponder(new AcpPermissionOutcome("cancelled", null));
        var (blocked, reason, nextCalled) = await Run(AcpApprovalMode.Ask, "bash", responder);
        Assert.True(blocked);
        Assert.Equal("cancelled", reason);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Ask_NoResponder_DegradesToAllow()
    {
        var (blocked, _, nextCalled) = await Run(AcpApprovalMode.Ask, "bash", responder: null);
        Assert.False(blocked);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task CustomAllowlist_AllowsListedToolInAskMode()
    {
        var responder = new ThrowingResponder();
        var (blocked, _, nextCalled) = await Run(AcpApprovalMode.Ask, "bash", responder, allowlist: ["bash"]);
        Assert.False(blocked);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task CustomAllowlist_BlocksUnlistedToolInAskMode()
    {
        var responder = new FakeResponder(new AcpPermissionOutcome("selected", "reject-once"));
        var (blocked, _, _) = await Run(AcpApprovalMode.Ask, "edit", responder, allowlist: ["bash"]);
        Assert.True(blocked);
    }

    private sealed class FakeResponder(AcpPermissionOutcome outcome) : IAcpPermissionResponder
    {
        public Task<AcpPermissionOutcome> RequestAsync(AcpToolCallUpdate toolCall, CancellationToken cancellationToken) => Task.FromResult(outcome);
        public void RejectAllPending(string reason) { }
    }

    private sealed class ThrowingResponder : IAcpPermissionResponder
    {
        public Task<AcpPermissionOutcome> RequestAsync(AcpToolCallUpdate toolCall, CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("permission round-trip should not have occurred");
        public void RejectAllPending(string reason) { }
    }
}
