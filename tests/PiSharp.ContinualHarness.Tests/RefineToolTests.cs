using System.Text.Json;
using PiSharp.ContinualHarness;
using PiSharp.ContinualHarness.Contracts;
using Xunit;

namespace PiSharp.ContinualHarness.Tests;

public sealed class RefineToolTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ch-tool-" + Guid.NewGuid().ToString("N"));

    public RefineToolTests() => Directory.CreateDirectory(_temp);
    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private (RefineTool Tool, HarnessTestHost.Host Host) Build(HarnessSettingsStub? settings = null, Func<bool>? gate = null)
    {
        settings ??= new HarnessSettingsStub();
        var host = HarnessTestHost.Create(_temp, settings);
        return (new RefineTool(host.Service, settings, gate), host);
    }

    private static JsonElement Args(string kind, string action, string name, string? evidence = null, bool? force = null)
    {
        var dict = new Dictionary<string, string> { ["kind"] = kind, ["action"] = action, ["name"] = name };
        if (evidence is not null) dict.Add("scope", "local");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            kind,
            action,
            name,
            content = name == "coding" ? new { markdown = "Always tab." } : null,
            evidence = evidence is null ? null : new[] { evidence },
            force,
        }));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Model_Apply_Journaled_With_Author_Model()
    {
        var (tool, host) = Build();
        var result = await tool.ExecuteAsync("1", Args("prompt", "create", "coding", evidence: "seen failure"));

        Assert.False(result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().First().Text.Contains("rejected", StringComparison.OrdinalIgnoreCase));
        var entry = host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"));
        Assert.NotNull(entry);
        var record = host.Local.Records.Single();
        Assert.Equal("model", record.Author);
        Assert.Single(record.Evidence);
    }

    [Fact]
    public async Task Rollback_Is_User_Only()
    {
        var (tool, host) = Build();
        var result = await tool.ExecuteAsync("1", JsonSerializer.SerializeToElement(new { kind = "prompt", action = "rollback", name = "x" }));
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().First().Text;
        Assert.Contains("user-only", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_Evidence_Rejected()
    {
        var (tool, host) = Build();
        var result = await tool.ExecuteAsync("1", JsonSerializer.SerializeToElement(new { kind = "prompt", action = "create", name = "n", content = new { markdown = "x" } }));
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().First().Text;
        Assert.Contains("rejected", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidence", text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(host.Local.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "n")));
    }

    [Fact]
    public async Task Disabled_When_Gate_Off()
    {
        var (tool, host) = Build(gate: () => false);
        var result = await tool.ExecuteAsync("1", Args("prompt", "create", "coding", evidence: "x"));
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().First().Text;
        Assert.Contains("disabled", text, StringComparison.OrdinalIgnoreCase);
    }
}
