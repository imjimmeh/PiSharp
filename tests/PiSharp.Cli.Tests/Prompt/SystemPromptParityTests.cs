using PiSharp.Agent.Core;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Tests.Modes;
using Xunit;

namespace PiSharp.Cli.Tests.Prompt;

public sealed class SystemPromptParityTests
{
    [Fact]
    public async Task PrintModePromptContainsDefaultParitySections()
    {
        AgentContext? observed = null;
        var runtime = await ModeTestRuntime.CreateAsync(
            stream: (_, context, _, _) => { observed = context; return ModeTestRuntime.StreamText("ok"); });

        await PrintMode.RunAsync(runtime, new PrintModeOptions(PrintOutputMode.Text, Messages: ["hello"]), new TestConsoleIO());

        Assert.Contains("Available tools:", observed!.SystemPrompt);
        Assert.Contains("Guidelines:", observed.SystemPrompt);
        Assert.Contains("Pi documentation", observed.SystemPrompt);
        Assert.Contains("Current date:", observed.SystemPrompt);
        Assert.Contains("Current working directory:", observed.SystemPrompt);
    }

    [Fact]
    public async Task PrintModePromptIncludesProjectContextFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-prompt-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "AGENTS.md"), "project rule");
        AgentContext? observed = null;
        var runtime = await ModeTestRuntime.CreateAsync(
            cwd: dir,
            stream: (_, context, _, _) => { observed = context; return ModeTestRuntime.StreamText("ok"); });

        await runtime.Harness.PromptAsync("hello");

        Assert.Contains("# Project Context", observed!.SystemPrompt);
        Assert.Contains("project rule", observed.SystemPrompt);
    }
}
