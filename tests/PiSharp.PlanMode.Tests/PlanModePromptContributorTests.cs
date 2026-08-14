using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.PlanMode;
using PiSharp.PlanMode.Tests.Fakes;
using Xunit;

namespace PiSharp.PlanMode.Tests;

public sealed class PlanModePromptContributorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-plan-mode-prompt", Guid.NewGuid().ToString("N"));

    public PlanModePromptContributorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static readonly string[] Restricted = ["read", "grep", "find", "ls"];

    private static SystemPromptCompositionContext CreateContext()
        => new(
            Cwd: "C:/project",
            CurrentDate: new DateOnly(2026, 8, 14),
            Mode: PromptMode.Default,
            Tools: [],
            SelectedToolNames: [],
            ExplicitGuidelines: [],
            CustomPrompt: null,
            AppendPrompt: null,
            ContextFiles: [],
            Skills: [],
            DocumentationPaths: new PromptDocumentationPaths("", "", ""));

    private static string SectionText(PromptContribution contribution)
        => Assert.IsType<RawPromptContent>(contribution.Section.Content).Text;

    private async Task<PlanModeService> CreateServiceAsync(
        PlanModeTestApi api,
        Func<string?, CancellationToken, Task<ModelDescriptor?>>? resolver = null,
        string? planningModel = null)
    {
        var service = new PlanModeService(api, new PlanFileStore(_root), "session-abcdefgh", resolver);
        await service.EnterAsync(new PlanModeOptions(Restricted, planningModel, _root, "session-abcdefgh"));
        return service;
    }

    [Fact]
    public async Task Planning_ContributesReadOnlyInstructionsSection()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreateServiceAsync(api);
        var contributor = new PlanModePromptContributor(service);

        var contribution = Assert.Single(contributor.Contribute(CreateContext()));
        Assert.Equal(PlanModePromptContributor.PlanningSectionId, contribution.Section.Id);
        Assert.Equal(PromptSectionKind.Instructions, contribution.Section.Kind);
        Assert.Equal("instructions", contribution.Section.Placement.Slot);
        Assert.Equal(100, contribution.Section.Placement.Priority);
        Assert.Equal(PlanModePromptContributor.SourceId, contribution.Source.Id);
        Assert.Equal(PromptContributionSourceKind.Extension, contribution.Source.Kind);

        var text = SectionText(contribution);
        Assert.Contains("read-only planning phase", text, StringComparison.Ordinal);
        Assert.Contains("read, grep, find, ls", text, StringComparison.Ordinal);
        Assert.Contains("MUST NOT modify any file", text, StringComparison.Ordinal);
        Assert.Contains("The plan will be written to:", text, StringComparison.Ordinal);
        Assert.Contains(service.PlanFile, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Planning model:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planning_WithPlanningModel_IncludesModelLine()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreateServiceAsync(api, resolver: (_, _) => Task.FromResult<ModelDescriptor?>(new ModelDescriptor("test", "planning-model", "api")), planningModel: "test/planning-model");
        var contributor = new PlanModePromptContributor(service);

        var text = SectionText(Assert.Single(contributor.Contribute(CreateContext())));

        Assert.Contains("Planning model: planning-model.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executing_ContributesApprovedPlanSection()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreateServiceAsync(api);
        await service.CapturePlanAsync("# Approved plan\n\nStep one, step two.");
        await service.ApproveAsync();
        var contributor = new PlanModePromptContributor(service);

        var contribution = Assert.Single(contributor.Contribute(CreateContext()));
        Assert.Equal(PlanModePromptContributor.ExecutingSectionId, contribution.Section.Id);
        var text = SectionText(contribution);
        Assert.Contains("Work must stay within the approved plan", text, StringComparison.Ordinal);
        Assert.Contains("# Approved plan", text, StringComparison.Ordinal);
        Assert.Contains("Step one, step two.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inactive_ContributesNothing()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = new PlanModeService(api, new PlanFileStore(_root), "session-abcdefgh");
        var contributor = new PlanModePromptContributor(service);

        Assert.Empty(contributor.Contribute(CreateContext()));
    }

    [Fact]
    public async Task Aborted_ContributesNothing()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreateServiceAsync(api);
        await service.CapturePlanAsync("body");
        await service.AbortAsync();
        var contributor = new PlanModePromptContributor(service);

        Assert.Empty(contributor.Contribute(CreateContext()));
    }

    [Fact]
    public async Task ContentIsReadLive_AfterTransition_NoReRegistration()
    {
        var api = new PlanModeTestApi { SessionName = "session-abcdefgh" };
        var service = await CreateServiceAsync(api);
        var contributor = new PlanModePromptContributor(service);

        Assert.Equal(PlanModePromptContributor.PlanningSectionId, Assert.Single(contributor.Contribute(CreateContext())).Section.Id);

        await service.CapturePlanAsync("body");
        await service.ApproveAsync();

        var contribution = Assert.Single(contributor.Contribute(CreateContext()));
        Assert.Equal(PlanModePromptContributor.ExecutingSectionId, contribution.Section.Id);
        Assert.Contains("body", SectionText(contribution), StringComparison.Ordinal);
    }
}
