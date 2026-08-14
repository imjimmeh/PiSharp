using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;
using PiSharp.PlanMode;
using PiSharp.PlanMode.Tests.Fakes;
using Xunit;

namespace PiSharp.PlanMode.Tests;

public sealed class PlanModeInputGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-plan-mode-gate", Guid.NewGuid().ToString("N"));

    public PlanModeInputGateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static ExtensionEvent InputEvent(string text)
        => new(
            ExtensionEventNames.Input,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input(text, null, "runtime")),
            new ExtensionInputEvent(text));

    private async Task<PlanModeService> CreatePlanningServiceAsync()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = new PlanModeService(api, new PlanFileStore(_root), "session-abcdefgh");
        await service.EnterAsync(new PlanModeOptions(["read", "grep", "find", "ls"], null, _root, "session-abcdefgh"));
        return service;
    }

    [Fact]
    public async Task MutationPrompt_WhilePlanning_IsTransformed()
    {
        var service = await CreatePlanningServiceAsync();
        var gate = new PlanModeInputGate(service);
        var evt = InputEvent("Please edit the file to fix the bug");

        await gate.OnInputAsync(evt, CancellationToken.None);

        Assert.NotNull(evt.InputResult);
        Assert.Equal("transform", evt.InputResult!.Action);
        Assert.NotNull(evt.InputResult.Text);
        Assert.Contains("Please edit the file to fix the bug", evt.InputResult.Text, StringComparison.Ordinal);
        Assert.Contains("[plan mode]", evt.InputResult.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyPrompt_WhilePlanning_IsNotTransformed()
    {
        var service = await CreatePlanningServiceAsync();
        var gate = new PlanModeInputGate(service);
        var evt = InputEvent("What does the parser do?");

        await gate.OnInputAsync(evt, CancellationToken.None);

        Assert.Null(evt.InputResult);
    }

    [Fact]
    public async Task UserBashLine_WhilePlanning_IsLeftUnrestricted()
    {
        var service = await CreatePlanningServiceAsync();
        var gate = new PlanModeInputGate(service);
        var evt = InputEvent("! git status");

        await gate.OnInputAsync(evt, CancellationToken.None);

        Assert.Null(evt.InputResult);
    }

    [Fact]
    public async Task OutsidePlanning_NoTransform()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = new PlanModeService(api, new PlanFileStore(_root), "session-abcdefgh");
        var gate = new PlanModeInputGate(service);
        var evt = InputEvent("please edit the file");

        await gate.OnInputAsync(evt, CancellationToken.None);

        Assert.Null(evt.InputResult);
    }

    [Fact]
    public async Task WhitespacePrompt_IsIgnored()
    {
        var service = await CreatePlanningServiceAsync();
        var gate = new PlanModeInputGate(service);
        var evt = InputEvent("   ");

        await gate.OnInputAsync(evt, CancellationToken.None);

        Assert.Null(evt.InputResult);
    }

    [Fact]
    public async Task NonInputPayload_IsIgnored()
    {
        var service = await CreatePlanningServiceAsync();
        var gate = new PlanModeInputGate(service);
        var evt = new ExtensionEvent(
            ExtensionEventNames.Input,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("text", null, "runtime")),
            "not an input event");

        await gate.OnInputAsync(evt, CancellationToken.None);

        Assert.Null(evt.InputResult);
    }

    [Theory]
    [InlineData("edit the file", true)]
    [InlineData("EDIT the file", true)]
    [InlineData("write a new module", true)]
    [InlineData("delete that folder", true)]
    [InlineData("run the tests", true)]
    [InlineData("update the docs", true)]
    [InlineData("read the source", false)]
    [InlineData("what does this function do", false)]
    [InlineData("explore the codebase", false)]
    public void RequiresTransform_DetectsMutationDirectives(string text, bool expected)
        => Assert.Equal(expected, PlanModeInputGate.RequiresTransform(text));
}
