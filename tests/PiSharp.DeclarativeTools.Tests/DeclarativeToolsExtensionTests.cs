using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.DeclarativeTools.Tests.Fakes;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.DeclarativeTools.Tests;

public sealed class DeclarativeToolsExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-declarative-tools-ext", Guid.NewGuid().ToString("N"));

    public DeclarativeToolsExtensionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private string CreateToolDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string dir, string fileName, string content)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
    }

    private sealed class Harness(ExtensionManager Manager, ExtensionRegistry Registry, FakeRuntimeSettings Settings, FakeExecutionEnv? Env, DeclarativeToolsExtension Extension)
    {
        public ExtensionManager Manager { get; } = Manager;
        public ExtensionRegistry Registry { get; } = Registry;
        public FakeRuntimeSettings Settings { get; } = Settings;
        public FakeExecutionEnv? Env { get; } = Env;
        public DeclarativeToolsExtension Extension { get; } = Extension;

        public IAgentTool Tool(string name)
            => Registry.Tools.First(t => t.Value.Name == name).Value;

        public bool HasTool(string name)
            => Registry.Tools.Any(t => t.Value.Name == name);
    }

    private async Task<Harness> CreateHarnessAsync(
        IReadOnlyDictionary<string, object?>? settings = null,
        string cwd = "C:/project",
        FakeExecutionEnv? env = null,
        IReadOnlySet<string>? builtInTools = null)
    {
        var registry = new ExtensionRegistry { BuiltInToolNames = builtInTools ?? new HashSet<string>(StringComparer.Ordinal) };
        var manager = new ExtensionManager(registry);
        var fakeSettings = new FakeRuntimeSettings();
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
                await fakeSettings.SetRawAsync("extensions.pisharp-declarative-tools." + key, value, ExtensionSettingsScope.Source);
        }

        var binding = new ExtensionRuntimeBinding(cwd, hasUi: false, NoExtensionUi.Instance)
        {
            RuntimeSettings = fakeSettings,
            ExecutionEnv = env,
            SendMessageAsync = (_, _, _, _) => Task.CompletedTask
        };

        var extension = new DeclarativeToolsExtension();
        var descriptor = new ExtensionDescriptor("pisharp-declarative-tools", "PiSharp Declarative Tools", "1.0.0");
        await manager.InitializeAsync(descriptor, extension, binding, CancellationToken.None);
        return new Harness(manager, registry, fakeSettings, env, extension);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task DeclarativeTools_RegisterInRegistry()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "hello.md", "---\ndescription: Greets the user.\n---\n");
        WriteFile(dir, "world.json", """{ "description": "Waves back." }""");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } });

        Assert.True(harness.HasTool("hello"));
        Assert.True(harness.HasTool("world"));
        Assert.All(harness.Registry.Tools.Where(t => t.Value.Name is "hello" or "world"),
            t => Assert.Equal("extension:pisharp-declarative-tools", t.SourceId));
        Assert.Equal(2, harness.Extension.LatestReport.Loaded.Count);
        Assert.Empty(harness.Extension.LatestReport.Skipped);
    }

    [Fact]
    public async Task DeclarativeToolInvocation_RejectsWithNoExecutableBody()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "hello.md", "---\ndescription: Greets the user.\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } });
        var tool = harness.Tool("hello");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.ExecuteAsync("call-1", JsonDocument.Parse("{}").RootElement, CancellationToken.None, null));
        Assert.Contains("no executable body", exception.Message);
    }

    [Fact]
    public async Task DuplicateName_FirstWinsWithReport()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "a.md", "---\nname: dup\ndescription: first\n---\n");
        WriteFile(dir, "b.md", "---\nname: dup\ndescription: second\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } });

        var loaded = harness.Registry.Tools.Where(t => t.Value.Name == "dup").ToArray();
        Assert.Single(loaded);
        var skipped = Assert.Single(harness.Extension.LatestReport.Skipped);
        Assert.Equal(ToolLoadStatus.Skipped, skipped.Status);
        Assert.Contains("Duplicate", skipped.Message);
    }

    [Fact]
    public async Task OverrideBuiltIn_IsHonored()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "read.md", "---\ndescription: overrides builtin\noverride: builtin\n---\n");

        var harness = await CreateHarnessAsync(
            new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } },
            builtInTools: new HashSet<string>(StringComparer.Ordinal) { "read" });

        Assert.True(harness.HasTool("read"));
    }

    [Fact]
    public async Task BuiltinCollisionUnderReject_SkipsToolNotRun()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "read.md", "---\ndescription: collides with builtin\n---\n");
        WriteFile(dir, "ok.md", "---\ndescription: fine\n---\n");

        var harness = await CreateHarnessAsync(
            new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } },
            builtInTools: new HashSet<string>(StringComparer.Ordinal) { "read" });

        Assert.False(harness.HasTool("read"));
        Assert.True(harness.HasTool("ok"));   // the collision must not kill the rest
        var skipped = Assert.Single(harness.Extension.LatestReport.Skipped);
        Assert.Contains("Registration failed", skipped.Message);
    }

    [Fact]
    public async Task ScriptTools_AreSkippedWithoutExecutionEnv()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "tool.sh", "echo hi\n");
        WriteFile(dir, "meta.md", "---\ndescription: declarative still loads\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } }, env: null);

        Assert.False(harness.HasTool("tool"));
        Assert.True(harness.HasTool("meta"));
        var skipped = Assert.Single(harness.Extension.LatestReport.Skipped);
        Assert.Contains("execution environment", skipped.Message);
    }

    [Fact]
    public async Task ScriptTools_ExecuteThroughExecutionEnv()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "greet.sh", "cat\n");
        var env = new FakeExecutionEnv("C:/project");
        env.EnqueueShellResult("hello there");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } }, env: env);

        Assert.True(harness.HasTool("greet"));
        var result = await harness.Tool("greet").ExecuteAsync(
            "call-1", JsonDocument.Parse("""{"name":"bob"}""").RootElement, CancellationToken.None, null);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Equal("hello there", text.Text);
        Assert.Contains(env.ExecutedCommands, e => e.Command.StartsWith("bash \""));
    }

    [Fact]
    public async Task SettingsChange_NewToolsDir_Rescans()
    {
        var dirA = CreateToolDir("a");
        var dirB = CreateToolDir("b");
        WriteFile(dirA, "tool-a.md", "---\ndescription: from a\n---\n");
        WriteFile(dirB, "tool-b.md", "---\ndescription: from b\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dirA } });
        Assert.True(harness.HasTool("tool-a"));

        await harness.Settings.SetRawAsync("extensions.pisharp-declarative-tools.toolsDir", new[] { dirB }, ExtensionSettingsScope.Source);

        await WaitUntilAsync(() => harness.HasTool("tool-b"));
        await WaitUntilAsync(() => !harness.HasTool("tool-a"));
        Assert.True(harness.Extension.LatestReport.Directories.Single().Equals(dirB, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SettingsChange_RemovedFile_Unregisters()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "ephemeral.md", "---\ndescription: temp\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } });
        Assert.True(harness.HasTool("ephemeral"));

        File.Delete(Path.Combine(dir, "ephemeral.md"));
        await harness.Settings.SetRawAsync("extensions.pisharp-declarative-tools.timeoutSeconds", 30, ExtensionSettingsScope.Source);

        await WaitUntilAsync(() => !harness.HasTool("ephemeral"));
    }

    [Fact]
    public async Task SettingsChange_UnrelatedKey_DoesNotRescan()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "stable.md", "---\ndescription: stays\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } });
        var timestamp = harness.Extension.LatestReport.Timestamp;

        await harness.Settings.SetRawAsync("logging.file", "some/path.jsonl", ExtensionSettingsScope.Source);
        await Task.Delay(200);

        Assert.Equal(timestamp, harness.Extension.LatestReport.Timestamp);
        Assert.True(harness.HasTool("stable"));
    }

    [Fact]
    public async Task Disabled_RegistersOnlyCommandAndReport()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "hello.md", "---\ndescription: should not load\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?>
        {
            ["enabled"] = false,
            ["toolsDir"] = new[] { dir }
        });

        Assert.Empty(harness.Registry.Tools);
        Assert.True(harness.Extension.LatestReport.Disabled);
        Assert.Contains(harness.Registry.Commands, c => c.Value.Name == "declarative-tools");
    }

    [Fact]
    public async Task Command_IsRegistered()
    {
        var harness = await CreateHarnessAsync();
        var command = Assert.Single(harness.Registry.Commands);
        Assert.Equal("declarative-tools", command.Value.Name);
        Assert.Equal("extension:pisharp-declarative-tools", command.SourceId);
    }

    [Fact]
    public async Task Unload_UnregistersAllTools()
    {
        var dir = CreateToolDir("tools");
        WriteFile(dir, "hello.md", "---\ndescription: gone after unload\n---\n");

        var harness = await CreateHarnessAsync(new Dictionary<string, object?> { ["toolsDir"] = new[] { dir } });
        Assert.True(harness.HasTool("hello"));

        harness.Manager.Unload("extension:pisharp-declarative-tools");

        Assert.False(harness.HasTool("hello"));
    }
}
