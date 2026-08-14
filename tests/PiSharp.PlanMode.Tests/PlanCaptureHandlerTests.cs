using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;
using PiSharp.PlanMode;
using PiSharp.PlanMode.Tests.Fakes;
using Xunit;

namespace PiSharp.PlanMode.Tests;

public sealed class PlanCaptureHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-plan-mode-capture", Guid.NewGuid().ToString("N"));

    public PlanCaptureHandlerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static readonly string[] Restricted = ["read", "grep", "find", "ls"];

    private static ExtensionEvent AgentEndEvent(params AgentMessage[] messages)
        => new(
            ExtensionEventNames.AgentEnd,
            new AgentHarnessEvent.Core(new AgentEvent.AgentEnd(messages)),
            new AgentEvent.AgentEnd(messages));

    private static AssistantMessage Assistant(params string[] texts)
        => new(texts.Select(text => (MessageContent)new TextContent(text)).ToArray());

    private async Task<PlanModeService> CreatePlanningServiceAsync(PlanModeTestApi api)
    {
        var service = new PlanModeService(api, new PlanFileStore(_root), "session-abcdefgh");
        await service.EnterAsync(new PlanModeOptions(Restricted, null, _root, "session-abcdefgh"));
        return service;
    }

    [Fact]
    public async Task AgentEnd_WhilePlanning_PersistsLastAssistantMessage()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreatePlanningServiceAsync(api);
        var handler = new PlanCaptureHandler(service);

        await handler.OnAgentEndAsync(AgentEndEvent(
            AgentMessages.User("go"),
            Assistant("first draft"),
            new AssistantMessage([]), // tool-call-only — must not win over the final text message
            Assistant("final plan body")), CancellationToken.None);

        Assert.Equal("final plan body", service.LastPlanBody);
        var contents = await new PlanFileStore(_root).ReadAsync(service.PlanFile);
        Assert.Equal(PlanFileStatus.Draft, contents.Status);
        Assert.Equal("final plan body", contents.Body.TrimEnd('\r', '\n'));
        Assert.Equal("session-abcdefgh", contents.SessionId);
    }

    [Fact]
    public async Task AgentEnd_ToolCallOnlyLastAssistant_IsIgnored()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreatePlanningServiceAsync(api);
        var handler = new PlanCaptureHandler(service);

        await handler.OnAgentEndAsync(AgentEndEvent(
            Assistant("earlier plan"),
            new AssistantMessage([])), CancellationToken.None);

        Assert.Null(service.LastPlanBody);
        Assert.False(File.Exists(service.PlanFile));
    }

    [Fact]
    public async Task AgentEnd_NoAssistantMessage_IsIgnored()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreatePlanningServiceAsync(api);
        var handler = new PlanCaptureHandler(service);

        await handler.OnAgentEndAsync(AgentEndEvent(AgentMessages.User("go")), CancellationToken.None);

        Assert.Null(service.LastPlanBody);
        Assert.False(File.Exists(service.PlanFile));
    }

    [Fact]
    public async Task AgentEnd_WhitespaceOnlyText_IsIgnored()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreatePlanningServiceAsync(api);
        var handler = new PlanCaptureHandler(service);

        await handler.OnAgentEndAsync(AgentEndEvent(Assistant("   ")), CancellationToken.None);

        Assert.Null(service.LastPlanBody);
        Assert.False(File.Exists(service.PlanFile));
    }

    [Fact]
    public async Task AgentEnd_OutsidePlanning_IsNoOp()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = new PlanModeService(api, new PlanFileStore(_root), "session-abcdefgh");
        var handler = new PlanCaptureHandler(service);

        await handler.OnAgentEndAsync(AgentEndEvent(Assistant("plan body")), CancellationToken.None);

        Assert.Null(service.LastPlanBody);
        Assert.False(File.Exists(service.PlanFile));
    }

    [Fact]
    public async Task AgentEnd_NonAgentEndPayload_IsIgnored()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreatePlanningServiceAsync(api);
        var handler = new PlanCaptureHandler(service);
        var evt = new ExtensionEvent(
            ExtensionEventNames.AgentEnd,
            new AgentHarnessEvent.Core(new AgentEvent.AgentEnd([])),
            "not an AgentEnd payload");

        await handler.OnAgentEndAsync(evt, CancellationToken.None);

        Assert.Null(service.LastPlanBody);
        Assert.False(File.Exists(service.PlanFile));
    }

    [Fact]
    public async Task Capture_OverwritesPreviousDraftOnSecondAgentEnd()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreatePlanningServiceAsync(api);
        var handler = new PlanCaptureHandler(service);

        await handler.OnAgentEndAsync(AgentEndEvent(Assistant("draft one")), CancellationToken.None);
        await handler.OnAgentEndAsync(AgentEndEvent(Assistant("draft two")), CancellationToken.None);

        Assert.Equal("draft two", service.LastPlanBody);
        var contents = await new PlanFileStore(_root).ReadAsync(service.PlanFile);
        Assert.Equal("draft two", contents.Body.TrimEnd('\r', '\n'));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void ExtractText_JoinsTextContentAndTrims()
    {
        var message = Assistant("  part one", "part two  ");

        Assert.Equal("part one\npart two", PlanCaptureHandler.ExtractText(message));
    }

    [Fact]
    public void ExtractText_EmptyWithoutTextContent()
    {
        Assert.Equal(string.Empty, PlanCaptureHandler.ExtractText(new AssistantMessage([])));
    }
}
