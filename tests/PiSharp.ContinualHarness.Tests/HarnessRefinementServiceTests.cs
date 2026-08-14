using System.Text.Json;
using PiSharp.ContinualHarness;
using PiSharp.ContinualHarness.Contracts;
using PiSharp.Agent.Core.Prompting;

using Xunit;

namespace PiSharp.ContinualHarness.Tests;

public sealed class HarnessRefinementServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ch-svc-" + Guid.NewGuid().ToString("N"));

    public HarnessRefinementServiceTests() => Directory.CreateDirectory(_temp);
    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private HarnessTestHost.Host Host(HarnessSettingsStub settings, StubEventBus? events = null, StubSessionApi? session = null)
        => HarnessTestHost.Create(_temp, settings, fixedClock: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), events: events, session: session);

    private static readonly RefinementEvidence Evidence = new("session-1", EntryId: null, "observed a failure");

    [Fact]
    public async Task Prompt_Create_Adds_And_Contributor_Emits_Section()
    {
        var settings = new HarnessSettingsStub();
        var host = Host(settings);
        var record = await host.Service.ApplyAsync(
            HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "coding",
            HarnessTestJson.Prompt("Always tab."), HarnessRefinementScope.Local, "user", [Evidence]);

        Assert.Equal(1, record.Version);
        var entry = host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"));
        Assert.NotNull(entry);

        var contributor = new HarnessPromptContributor(() => host.Local.Effective.Values);
        var contributed = contributor.Contribute(null!).ToList();
        var section = contributed.Single(c => c.Section.Id == "harness.prompt.coding");
        Assert.Equal("Always tab.", ((RawPromptContent)section.Section.Content).Text);
    }

    [Fact]
    public async Task Prompt_Update_Replaces_Content()
    {
        var settings = new HarnessSettingsStub();
        var host = Host(settings);
        await host.Service.ApplyAsync(HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "coding", HarnessTestJson.Prompt("v1"), HarnessRefinementScope.Local, "user", [Evidence]);
        await host.Service.ApplyAsync(HarnessRefinementKind.Prompt, HarnessRefinementAction.Update, "coding", HarnessTestJson.Prompt("v2"), HarnessRefinementScope.Local, "user", [Evidence]);

        var entry = host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"));
        Assert.Equal(2, entry.Version);
        Assert.Equal("v2", entry.Content.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task Prompt_Delete_Removes_Section()
    {
        var settings = new HarnessSettingsStub();
        var host = Host(settings);
        await host.Service.ApplyAsync(HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "coding", HarnessTestJson.Prompt("v1"), HarnessRefinementScope.Local, "user", [Evidence]);
        var del = await host.Service.ApplyAsync(HarnessRefinementKind.Prompt, HarnessRefinementAction.Delete, "coding", content: null, HarnessRefinementScope.Local, "user", [Evidence]);

        Assert.True(del.Deleted);
        Assert.Null(host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding")));
        var contributor = new HarnessPromptContributor(() => host.Local.Effective.Values);
        Assert.DoesNotContain(contributor.Contribute(null!), c => c.Section.Id == "harness.prompt.coding");
    }

    [Fact]
    public async Task Missing_Evidence_Rejected_When_Required()
    {
        var settings = new HarnessSettingsStub { RequireEvidence = true };
        var host = Host(settings);
        var ex = await Assert.ThrowsAsync<HarnessRejectedException>(() => host.Service.ApplyAsync(
            HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "coding",
            HarnessTestJson.Prompt("v1"), HarnessRefinementScope.Local, "user", []));
        Assert.Contains("evidence", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disallowed_Kind_Rejected()
    {
        var settings = new HarnessSettingsStub { AllowedKinds = ["prompt"] };
        var host = Host(settings);
        var ex = await Assert.ThrowsAsync<HarnessRejectedException>(() => host.Service.ApplyAsync(
            HarnessRefinementKind.Skill, HarnessRefinementAction.Create, "skill",
            HarnessTestJson.Skill("d", "c"), HarnessRefinementScope.Global, "user", [Evidence]));
        Assert.Contains("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Local_Skill_Scope_Rejected()
    {
        var settings = new HarnessSettingsStub();
        var host = Host(settings);
        var ex = await Assert.ThrowsAsync<HarnessRejectedException>(() => host.Service.ApplyAsync(
            HarnessRefinementKind.Skill, HarnessRefinementAction.Create, "skill",
            HarnessTestJson.Skill("d", "c"), HarnessRefinementScope.Local, "user", [Evidence]));
        Assert.Contains("global", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rollback_Restores_Prior_Version()
    {
        var settings = new HarnessSettingsStub();
        var host = Host(settings);
        await host.Service.ApplyAsync(HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "coding", HarnessTestJson.Prompt("v1"), HarnessRefinementScope.Local, "user", [Evidence]);
        await host.Service.ApplyAsync(HarnessRefinementKind.Prompt, HarnessRefinementAction.Update, "coding", HarnessTestJson.Prompt("v2"), HarnessRefinementScope.Local, "user", [Evidence]);

        var rollback = await host.Service.RollbackAsync(HarnessRefinementKind.Prompt, "coding", targetVersion: 1, HarnessRefinementScope.Local, "user");

        Assert.Equal(HarnessRefinementAction.Rollback, rollback.Action);
        Assert.Equal(1, rollback.TargetVersion);
        Assert.Equal("v1", host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"))!.Content.GetProperty("markdown").GetString());
        Assert.Equal(3, host.Local.History(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding")).Count);

    }

    // --- Clobber protection (subagent file target) ------------------------

    [Fact]
    public async Task Host_Edit_Then_Update_Conflicts_Unless_Force()
    {
        var settings = new HarnessSettingsStub { ConflictPolicy = "reject" };
        var host = Host(settings);
        var key = new HarnessEntryKey(HarnessRefinementKind.Subagent, "reviewer");

        await host.Service.ApplyAsync(HarnessRefinementKind.Subagent, HarnessRefinementAction.Create, "reviewer",
            HarnessTestJson.Subagent("---\nname: reviewer\ndescription: reviews\n---\nbody\n"), HarnessRefinementScope.Local, "user", [Evidence]);

        // Host edits the on-disk definition behind the daemon's back.
        var file = Path.Combine(_temp, "agents", "reviewer.md");
        Assert.True(File.Exists(file));
        File.WriteAllText(file, "---\nname: reviewer\ndescription: reviews\n---\nHOST EDITED\n");

        var ex = await Assert.ThrowsAsync<HarnessConflictException>(() => host.Service.ApplyAsync(
            HarnessRefinementKind.Subagent, HarnessRefinementAction.Update, "reviewer",
            HarnessTestJson.Subagent("---\nname: reviewer\ndescription: reviews\n---\nnew\n"), HarnessRefinementScope.Local, "user", [Evidence]));
        Assert.Equal(key, ex.Key);
        Assert.Equal(file, ex.TargetPath, ignoreCase: true);

        // Force applies despite the conflict.
        var record = await host.Service.ApplyAsync(
            HarnessRefinementKind.Subagent, HarnessRefinementAction.Update, "reviewer",
            HarnessTestJson.Subagent("---\nname: reviewer\ndescription: reviews\n---\nnew\n"), HarnessRefinementScope.Local, "user", [Evidence], force: true);
        Assert.Equal(2, record.Version);
    }

    [Fact]
    public async Task Resync_Marks_Dirty_And_Rebases()
    {
        var settings = new HarnessSettingsStub();
        var host = Host(settings);
        await host.Service.ApplyAsync(HarnessRefinementKind.Subagent, HarnessRefinementAction.Create, "reviewer",
            HarnessTestJson.Subagent("---\nname: reviewer\ndescription: reviews\n---\nbody\n"), HarnessRefinementScope.Local, "user", [Evidence]);

        var file = Path.Combine(_temp, "agents", "reviewer.md");
        File.WriteAllText(file, "---\nname: reviewer\ndescription: reviews\n---\nHOST WRITE\n");

        var entry = await host.Service.ReSyncTargetAsync(new HarnessEntryKey(HarnessRefinementKind.Subagent, "reviewer"));
        Assert.True(entry.Dirty);
        Assert.Contains("HOST WRITE", entry.Content.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task Create_Collision_On_Existing_File_Conflicts()
    {
        var settings = new HarnessSettingsStub { ConflictPolicy = "reject" };
        var host = Host(settings);
        await host.Service.ApplyAsync(HarnessRefinementKind.Subagent, HarnessRefinementAction.Create, "reviewer",
            HarnessTestJson.Subagent("---\nname: reviewer\ndescription: reviews\n---\nbody\n"), HarnessRefinementScope.Local, "user", [Evidence]);

        var ex = await Assert.ThrowsAsync<HarnessConflictException>(() => host.Service.ApplyAsync(
            HarnessRefinementKind.Subagent, HarnessRefinementAction.Create, "reviewer",
            HarnessTestJson.Subagent("---\nname: reviewer\ndescription: reviews\n---\ndup\n"), HarnessRefinementScope.Local, "user", [Evidence], force: false));
        Assert.Contains("collision", ex.Diff, StringComparison.OrdinalIgnoreCase);
    }

    // --- Events + session audit -------------------------------------------

    [Fact]
    public async Task Apply_Emits_Events_And_Audits_Session()
    {
        var settings = new HarnessSettingsStub();
        var events = new StubEventBus();
        var session = new StubSessionApi();
        var host = Host(settings, events, session);

        await host.Service.ApplyAsync(HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "coding",
            HarnessTestJson.Prompt("v1"), HarnessRefinementScope.Local, "user", [Evidence]);

        Assert.Contains(events.Emitted, e => e.Name == HarnessRefinementService.HarnessRefinementApplied);
        Assert.Contains(events.Emitted, e => e.Name == HarnessRefinementService.HarnessStateChanged);
        Assert.Single(session.Audits);
        Assert.Equal("harness.refinement", session.Audits[0].CustomType);
    }

    // --- Skill-kind via fake managed API -----------------------------------

    [Fact]
    public async Task Skill_Kind_Writes_Through_Managed_Skill_Api()
    {
        var settings = new HarnessSettingsStub();
        var api = new FakeManagedSkillApi();
        var host = HarnessTestHost.Create(_temp, settings,
            targetFactory: (kind, scope) => kind == HarnessRefinementKind.Skill
                ? new ManagedSkillTarget(api)
                : new PromptSectionTarget());

        var record = await host.Service.ApplyAsync(HarnessRefinementKind.Skill, HarnessRefinementAction.Create, "style",
            HarnessTestJson.Skill("coding style", "Use 2-space indent."), HarnessRefinementScope.Global, "user", [Evidence]);

        Assert.Equal("style", record.Name);
        Assert.True(api.Skills.ContainsKey("style"));
        Assert.Equal("Use 2-space indent.", api.Skills["style"].Content);
    }

    // --- Memory-kind via fake store ----------------------------------------

    [Fact]
    public async Task Memory_Kind_RoundTrips_Through_Store()
    {
        var settings = new HarnessSettingsStub();
        var store = new FakeMemoryStore();
        var host = HarnessTestHost.Create(_temp, settings,
            targetFactory: (kind, scope) => kind == HarnessRefinementKind.Memory
                ? new MemoryTarget(store)
                : new PromptSectionTarget());

        await host.Service.ApplyAsync(HarnessRefinementKind.Memory, HarnessRefinementAction.Create, "lesson",
            HarnessTestJson.Memory("Lesson", "Always back up."), HarnessRefinementScope.Local, "user", [Evidence]);
        Assert.True(store.Records.ContainsKey("refine/lesson"));

        await host.Service.ApplyAsync(HarnessRefinementKind.Memory, HarnessRefinementAction.Delete, "lesson",
            content: null, HarnessRefinementScope.Local, "user", [Evidence]);
        Assert.False(store.Records.ContainsKey("refine/lesson"));
    }

    // --- Validation ---------------------------------------------------------

    [Fact]
    public async Task NonSlug_Name_Rejected()
    {
        var settings = new HarnessSettingsStub();
        var host = Host(settings);
        var ex = await Assert.ThrowsAsync<HarnessRejectedException>(() => host.Service.ApplyAsync(
            HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "bad/name",
            HarnessTestJson.Prompt("v1"), HarnessRefinementScope.Local, "user", [Evidence]));
        Assert.Contains("slug", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Content_Over_Max_Rejected()
    {
        var settings = new HarnessSettingsStub { MaxContentBytes = 16 };
        var host = Host(settings);
        var ex = await Assert.ThrowsAsync<HarnessRejectedException>(() => host.Service.ApplyAsync(
            HarnessRefinementKind.Prompt, HarnessRefinementAction.Create, "big",
            HarnessTestJson.Prompt(new string('x', 200)), HarnessRefinementScope.Local, "user", [Evidence]));
        Assert.Contains("maxContentBytes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
