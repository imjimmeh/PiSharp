using System.Text.Json;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
using PiSharp.Memory.Abstractions;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class MemoryExtensionTests
{
    private static string ContentOf(AgentToolResult<object?> result)
        => string.Join("\n", result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Select(c => c.Text));

    // --- zero footprint when disabled ---

    [Fact]
    public async Task Disabled_RegistersNoToolsCommandOrContributor()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?> { ["enabled"] = false });

        Assert.Empty(harness.Registry.Tools);
        Assert.Null(harness.FindCommand("memory"));
        Assert.Null(harness.PromptContributor);
    }

    [Fact]
    public async Task DisabledByDefault_RegistersNothing()
    {
        var harness = await MemoryHarness.CreateAsync();

        Assert.Empty(harness.Registry.Tools);
        Assert.Null(harness.FindCommand("memory"));
    }

    // --- registration + blocked gating when backend off ---

    [Fact]
    public async Task EnabledWithOffBackend_RegistersAllFiveTools_BlockedOnUse()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "off"
        });



        var result = await harness.Tool("retain").ExecuteAsync(
            "call-1",
            JsonSerializer.SerializeToElement(new { title = "A", content = "B" }),
            CancellationToken.None,
            null);
        Assert.Equal(["learn", "memory_edit", "recall", "reflect", "retain"],
            harness.Registry.Tools.Select(tool => tool.Value.Name).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task EnabledWithUnknownBackend_FallsBackToOff()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "holographic"
        });

        Assert.Equal("off", harness.Extension.Store!.Provider.Id);
    }

    // --- end-to-end through registered tools (file backend) ---

    [Fact]
    public async Task RetainThenRecall_RoundTripsThroughRegisteredTools()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });

        var retain = await harness.Tool("retain").ExecuteAsync(
            "call-1",
            JsonSerializer.SerializeToElement(new { title = "OAuth", content = "device flow via --login", recordKey = "facts/oauth-setup", tags = new[] { "oauth" } }),
            CancellationToken.None,
            null);
        Assert.DoesNotContain("backend is off", ContentOf(retain));

        var recall = await harness.Tool("recall").ExecuteAsync(
            "call-2",
            JsonSerializer.SerializeToElement(new { query = "oauth" }),
            CancellationToken.None,
            null);
        var recallText = ContentOf(recall);
        Assert.Contains("facts/oauth-setup", recallText);
        Assert.Contains("device flow via --login", recallText);
    }

    [Fact]
    public async Task Retain_SameKeyTwiceThroughTool_IsIdempotent()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });

        var args = JsonSerializer.SerializeToElement(new { title = "OAuth", content = "v1", recordKey = "facts/oauth-setup" });
        await harness.Tool("retain").ExecuteAsync("call-1", args, CancellationToken.None, null);
        await harness.Tool("retain").ExecuteAsync("call-2", JsonSerializer.SerializeToElement(new { title = "OAuth", content = "v2", recordKey = "facts/oauth-setup" }), CancellationToken.None, null);

        Assert.Single(await harness.Extension.Store!.ListAsync(MemoryScope.Project, new MemoryQuery()));
    }

    // --- settings gating / backend swap ---

    [Fact]
    public async Task BackendSwap_OffToFile_TakesEffectOnNextUse()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "off"
        });

        await harness.SetSettingAsync("backend", "file");
        await MemoryTestHelpers.WaitUntilAsync(() => harness.Extension.Store?.Provider.Id == "file");

        var result = await harness.Tool("retain").ExecuteAsync(
            "call-1",
            JsonSerializer.SerializeToElement(new { title = "A", content = "B", recordKey = "facts/a" }),
            CancellationToken.None,
            null);
        Assert.DoesNotContain("backend is off", ContentOf(result));
        Assert.NotNull(await harness.Extension.Store!.GetAsync(MemoryScope.Project, "facts/a"));
    }

    [Fact]
    public async Task BackendSwap_EmitsBackendChangedEvent()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "off"
        });

        await harness.SetSettingAsync("backend", "file");
        await MemoryTestHelpers.WaitUntilAsync(() => harness.Extension.Store?.Provider.Id == "file");

        var emitted = harness.EmittedEvents.FirstOrDefault(entry => entry.Name == MemoryEventNames.MemoryBackendChanged);
        Assert.NotEqual(default, emitted);
    }

    // --- prompt injection (mental models) ---

    [Fact]
    public async Task PromptInjection_FirstAgentStart_EmitsMentalModels()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord(
            "mental-models/oauth", MemoryKind.MentalModel, "OAuth", "device flow", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await harness.FireEventAsync("agent_start", null);

        var contributions = harness.PromptContributor!.Contribute(TestPromptContext()).ToArray();
        var section = Assert.Single(contributions).Section;
        Assert.Equal(MentalModelPromptContributor.SectionId, section.Id);
        Assert.Contains("mental-models/oauth", Assert.IsType<PiSharp.Agent.Core.Prompting.MarkdownPromptContent>(section.Content).Markdown);
    }

    [Fact]
    public async Task PromptInjection_OnlyOncePerSession()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord(
            "mental-models/m1", MemoryKind.MentalModel, "M", "content", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await harness.FireEventAsync("agent_start", null);
        Assert.Single(harness.PromptContributor!.Contribute(TestPromptContext()));

        // A second agent_start in the same session must not inject again.
        await harness.FireEventAsync("agent_start", null);
        Assert.Empty(harness.PromptContributor!.Contribute(TestPromptContext()));

        // A fresh session resets the flag.
        await harness.FireEventAsync("session_start", null);
        await harness.FireEventAsync("agent_start", null);
        Assert.Single(harness.PromptContributor!.Contribute(TestPromptContext()));
    }

    [Fact]
    public async Task PromptInjection_EmptyMemory_InjectsNothing()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });

        await harness.FireEventAsync("agent_start", null);

        Assert.Empty(harness.PromptContributor!.Contribute(TestPromptContext()));
    }

    [Fact]
    public async Task PromptInjection_OnlyMentalModels_NotFacts()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord(
            "facts/plain", MemoryKind.Fact, "Fact", "content", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await harness.FireEventAsync("agent_start", null);

        Assert.Empty(harness.PromptContributor!.Contribute(TestPromptContext()));
    }

    private static PiSharp.Agent.Core.Prompting.SystemPromptCompositionContext TestPromptContext() => new(
        Cwd: "/",
        CurrentDate: DateOnly.FromDateTime(DateTime.UtcNow),
        Mode: PromptMode.Default,
        Tools: [],
        SelectedToolNames: [],
        ExplicitGuidelines: [],
        CustomPrompt: null,
        AppendPrompt: null,
        ContextFiles: [],
        Skills: [],
        DocumentationPaths: new PiSharp.Agent.Core.Prompting.PromptDocumentationPaths("README.md", "docs", "examples"));
}
