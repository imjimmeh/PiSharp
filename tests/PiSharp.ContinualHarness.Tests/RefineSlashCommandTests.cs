using PiSharp.ContinualHarness;
using PiSharp.ContinualHarness.Contracts;
using Xunit;

namespace PiSharp.ContinualHarness.Tests;

public sealed class RefineSlashCommandTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ch-cmd-" + Guid.NewGuid().ToString("N"));

    public RefineSlashCommandTests() => Directory.CreateDirectory(_temp);
    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private (RefineSlashCommand Command, HarnessTestHost.Host Host, StubApi Api, StubUi Ui) Build(Func<bool>? gate = null)
    {
        var settings = new HarnessSettingsStub();
        var host = HarnessTestHost.Create(_temp, settings);
        var api = new StubApi { Ui = new StubUi() };
        var command = new RefineSlashCommand(api, host.Service, settings, gate);
        return (command, host, api, (StubUi)api.Ui);
    }

    [Fact]
    public void Tokenize_Handles_Quotes()
    {
        var tokens = RefineSlashCommand.Tokenize("prompt create \"my name\" --evidence=\"a b\"");
        Assert.Equal(new[] { "prompt", "create", "my name", "--evidence=a b" }, tokens);
    }

    [Fact]
    public async Task Direct_Create_Applies()
    {
        var (command, host, _, ui) = Build();
        await command.InvokeAsync("prompt create coding \"Always tab.\" --evidence=\"observed x\"");

        var entry = host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"));
        Assert.NotNull(entry);
        Assert.Equal("Always tab.", entry.Content.GetProperty("markdown").GetString());
        Assert.Contains(ui.Notifications, n => n.Message.Contains("Refined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task List_Shows_Entries()
    {
        var (command, host, _, ui) = Build();
        await command.InvokeAsync("prompt create coding \"v1\" --evidence=\"x\"");
        await command.InvokeAsync("prompt create style \"v1\" --evidence=\"x\"");

        await command.InvokeAsync("list");

        var last = ui.Notifications[^1].Message;
        Assert.Contains("prompt/coding", last);
        Assert.Contains("prompt/style", last);
    }

    [Fact]
    public async Task Show_Renders_Record_And_Content()
    {
        var (command, host, _, ui) = Build();
        await command.InvokeAsync("prompt create coding \"v1\" --evidence=\"x\"");

        await command.InvokeAsync("show prompt coding");

        var last = ui.Notifications[^1].Message;
        Assert.Contains("#1", last);
        Assert.Contains("v1", last);
    }

    [Fact]
    public async Task Diff_Renders_Between_Versions()
    {
        var (command, host, _, ui) = Build();
        await command.InvokeAsync("prompt create coding \"line1\nline2\" --evidence=\"x\"");
        await command.InvokeAsync("prompt update coding \"line1\nline3\" --evidence=\"x\"");

        await command.InvokeAsync("diff prompt coding");

        var last = ui.Notifications[^1].Message;
        Assert.Contains("v1", last);
        Assert.Contains("v2", last);
    }

    [Fact]
    public async Task Rollback_Subcommand_Restores()
    {
        var (command, host, _, _) = Build();
        await command.InvokeAsync("prompt create coding \"v1\" --evidence=\"x\"");
        await command.InvokeAsync("prompt update coding \"v2\" --evidence=\"x\"");
        Assert.Equal("v2", host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"))!.Content.GetProperty("markdown").GetString());

        await command.InvokeAsync("rollback prompt coding 1");

        Assert.Equal("v1", host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"))!.Content.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task Sync_Subcommand_Resyncs_File_Target()
    {
        var (command, host, _, ui) = Build();
        await command.InvokeAsync("subagent create reviewer \"---\nname: reviewer\ndescription: reviews\n---\nbody\" --evidence=\"x\"");
        var file = Path.Combine(_temp, "agents", "reviewer.md");
        File.WriteAllText(file, "---\nname: reviewer\ndescription: reviews\n---\nHOST WRITE\n");

        await command.InvokeAsync("sync");

        var entry = host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Subagent, "reviewer"));
        Assert.True(entry.Dirty);
        Assert.Contains("Re-synced 1", ui.Notifications[^1].Message);
    }

    [Fact]
    public async Task Gate_Disabled_NoOps()
    {
        var (command, host, _, ui) = Build(gate: () => false);
        await command.InvokeAsync("prompt create coding \"v1\" --evidence=\"x\"");
        Assert.Null(host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding")));
        Assert.Contains(ui.Notifications, n => n.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }
}
