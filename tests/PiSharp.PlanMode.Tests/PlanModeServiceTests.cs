using PiSharp.Agent.Core.Models;
using PiSharp.PlanMode;
using PiSharp.PlanMode.Tests.Fakes;
using Xunit;

namespace PiSharp.PlanMode.Tests;

public sealed class PlanModeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-plan-mode-service", Guid.NewGuid().ToString("N"));

    public PlanModeServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private sealed record PlanModeChangedPayload(
        string phase,
        IReadOnlyList<string> restrictedToolNames,
        string? planningModel,
        string? planFile);

    private static readonly string[] DefaultRestricted = ["read", "grep", "find", "ls"];

    private static PlanModeTestApi CreateApi()
    {
        var api = new PlanModeTestApi
        {
            RegisteredToolNames = ["read", "grep", "find", "ls", "edit", "write", "bash", "web_search"],
            CurrentModel = new ModelDescriptor("test", "original-model", "api"),
            SessionName = "session-abcdefgh"
        };
        return api;
    }

    private PlanModeTestApi Api { get; } = CreateApi();

    private PlanModeService CreateService(Func<string?, CancellationToken, Task<ModelDescriptor?>>? resolver = null)
        => new(Api, new PlanFileStore(_root), "session-abcdefgh", resolver);

    private static PlanModeChangedPayload LastEvent(PlanModeTestApi api)
    {
        var evt = Assert.Single(api.ClientEvents);
        Assert.Equal(PlanModeService.PlanModeChangedEvent, evt.Name);
        return System.Text.Json.JsonSerializer.Deserialize<PlanModeChangedPayload>(
            System.Text.Json.JsonSerializer.Serialize(evt.Payload))!;
    }

    [Fact]
    public async Task Enter_RestrictsToolsToEffectiveSet_EmitsOneEvent()
    {
        var service = CreateService();
        var options = new PlanModeOptions(
            ["read", "grep", "find", "ls", "write", "nonexistent"],
            null,
            _root,
            "session-abcdefgh");

        var state = await service.EnterAsync(options);

        Assert.Equal(PlanModePhase.Planning, state.Phase);
        Assert.Equal(PlanModePhase.Planning, service.Phase);
        Assert.Equal(["read", "grep", "find", "ls", "write", "web_search"], state.RestrictedToolNames);
        Assert.Null(state.PlanningModel);
        Assert.NotNull(state.PlanFile);

        var setCall = Assert.Single(Api.SetActiveToolsCalls);
        Assert.Equal(["read", "grep", "find", "ls", "write", "web_search"], setCall);

        var payload = LastEvent(Api);
        Assert.Equal("planning", payload.phase);
        Assert.Equal(["read", "grep", "find", "ls", "write", "web_search"], payload.restrictedToolNames);
        Assert.Null(payload.planningModel);
        Assert.Equal(state.PlanFile, payload.planFile);
    }

    [Fact]
    public async Task Enter_AppliesResolvedPlanningModel_RecordsId()
    {
        var planningModel = new ModelDescriptor("test", "planning-model", "api");
        var service = CreateService((_, _) => Task.FromResult<ModelDescriptor?>(planningModel));

        var state = await service.EnterAsync(new PlanModeOptions(DefaultRestricted, "test/planning-model", _root, "session-abcdefgh"));

        Assert.Equal("planning-model", state.PlanningModel);
        Assert.Equal(planningModel, Assert.Single(Api.SetModelCalls));
        Assert.Equal("planning-model", LastEvent(Api).planningModel);
    }

    [Fact]
    public async Task Enter_UnresolvablePlanningModel_DoesNotSwitchModel()
    {
        var service = CreateService((_, _) => Task.FromResult<ModelDescriptor?>(null));

        var state = await service.EnterAsync(new PlanModeOptions(DefaultRestricted, "test/unknown", _root, "session-abcdefgh"));

        Assert.Null(state.PlanningModel);
        Assert.Empty(Api.SetModelCalls);
        Assert.Equal("original-model", Api.CurrentModel!.Id);
    }

    [Fact]
    public async Task Enter_ThrowsWhenAlreadyPlanningOrExecuting()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh")));

        await service.CapturePlanAsync("body");
        await service.ApproveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh")));
    }

    [Fact]
    public async Task Capture_WritesDraftFileAndExposesApprovalCandidate()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));
        Api.ClientEvents.Clear();

        await service.CapturePlanAsync("# The plan\n\nStep one.");

        Assert.Equal("# The plan\n\nStep one.", service.LastPlanBody);
        Assert.True(File.Exists(service.PlanFile));
        var contents = await new PlanFileStore(_root).ReadAsync(service.PlanFile);
        Assert.Equal(PlanFileStatus.Draft, contents.Status);
        Assert.Equal("# The plan\n\nStep one.", contents.Body.TrimEnd('\r', '\n'));
        Assert.Empty(Api.ClientEvents); // capture is not a phase transition
    }

    [Fact]
    public async Task Capture_OutsidePlanning_IsNoOp()
    {
        var service = CreateService();

        await service.CapturePlanAsync("body");

        Assert.Null(service.LastPlanBody);
        Assert.False(File.Exists(service.PlanFile));
    }

    [Fact]
    public async Task Approve_RequiresCapturedBody()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync());
    }

    [Fact]
    public async Task Approve_RestoresToolsAndModel_MarksFileApproved_Emits()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));
        await service.CapturePlanAsync("approved body");
        Api.ClientEvents.Clear();
        Api.SetActiveToolsCalls.Clear();
        Api.SetModelCalls.Clear();

        var state = await service.ApproveAsync();

        Assert.Equal(PlanModePhase.Executing, state.Phase);
        Assert.Equal("approved body", service.LastPlanBody);
        Assert.Equal(PlanFileStatus.Approved, (await new PlanFileStore(_root).ReadAsync(service.PlanFile)).Status);
        Assert.Equal("approved body", (await new PlanFileStore(_root).ReadAsync(service.PlanFile)).Body.TrimEnd('\r', '\n'));
        Assert.Null(Assert.Single(Api.SetActiveToolsCalls)); // null restores full set
        Assert.Equal("original-model", Assert.Single(Api.SetModelCalls).Id);
        var payload = LastEvent(Api);
        Assert.Equal("executing", payload.phase);
        Assert.Empty(payload.restrictedToolNames);
    }

    [Fact]
    public async Task Abort_RestoresToolsAndModel_MarksFileAborted_Emits()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));
        await service.CapturePlanAsync("aborted body");
        Api.ClientEvents.Clear();
        Api.SetActiveToolsCalls.Clear();
        Api.SetModelCalls.Clear();

        var state = await service.AbortAsync();

        Assert.Equal(PlanModePhase.Aborted, state.Phase);
        Assert.Equal(PlanFileStatus.Aborted, (await new PlanFileStore(_root).ReadAsync(service.PlanFile)).Status);
        Assert.Null(Assert.Single(Api.SetActiveToolsCalls));
        Assert.Equal("original-model", Assert.Single(Api.SetModelCalls).Id);
        Assert.Equal("aborted", LastEvent(Api).phase);
    }

    [Fact]
    public async Task Abort_WithoutCapturedPlan_Throws()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.AbortAsync());
    }

    [Fact]
    public async Task End_ReturnsToInactive_StopsInjectingBody()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));
        await service.CapturePlanAsync("body");
        await service.ApproveAsync();
        Api.ClientEvents.Clear();

        var state = await service.EndAsync();

        Assert.Equal(PlanModePhase.Inactive, state.Phase);
        Assert.Null(service.LastPlanBody);
        Assert.Equal("inactive", LastEvent(Api).phase);
    }

    [Fact]
    public async Task Transitions_FromWrongPhase_Throw()
    {
        var service = CreateService();
        await service.EnterAsync(new PlanModeOptions(DefaultRestricted, null, _root, "session-abcdefgh"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EndAsync());
        await service.CapturePlanAsync("body");
        await service.ApproveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AbortAsync());
    }

    [Theory]
    [InlineData("read,grep,find,ls,edit,write,bash,web_search", "read,grep,find,ls,web_search")]
    [InlineData("read,grep", "read,grep")]
    [InlineData("read,web_search", "read,web_search")]
    [InlineData("", "")]
    public void ComputeEffectiveRestrictedTools_IntersectsRegistered_AddsWebSearchWhenPresent(string registeredCsv, string expectedCsv)
    {
        var restricted = new[] { "read", "grep", "find", "ls", "web_search", "nonexistent" };
        var registered = registeredCsv.Length == 0 ? [] : registeredCsv.Split(',');
        var effective = PlanModeService.ComputeEffectiveRestrictedTools(restricted, registered);

        Assert.Equal(expectedCsv, string.Join(",", effective));
    }

    [Fact]
    public void ComputeEffectiveRestrictedTools_DropsDuplicates()
    {
        var effective = PlanModeService.ComputeEffectiveRestrictedTools(["read", "read", "grep"], ["read", "grep"]);

        Assert.Equal(["read", "grep"], effective);
    }

    [Theory]
    [InlineData(PlanModePhase.Inactive, "inactive")]
    [InlineData(PlanModePhase.Planning, "planning")]
    [InlineData(PlanModePhase.Executing, "executing")]
    [InlineData(PlanModePhase.Aborted, "aborted")]
    public void PhaseToString_MapsAllPhases(PlanModePhase phase, string expected)
        => Assert.Equal(expected, PlanModeService.PhaseToString(phase));
}
