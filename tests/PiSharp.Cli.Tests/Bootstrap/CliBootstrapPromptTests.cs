using PiSharp.Agent.Core;
using PiSharp.Agent.Resources;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Parsing;
using PiSharp.Cli.Tests.Modes;
using Xunit;

namespace PiSharp.Cli.Tests.Bootstrap;

public sealed class CliBootstrapPromptTests
{
    [Fact]
    public async Task BootstrapPassesCliSystemPromptAndAppendPromptToHarnessOptions()
    {
        AgentContext? observed = null;
        var runtime = await ModeTestRuntime.CreateAsync(
            args: new CliArgs(SystemPrompt: "CUSTOM", AppendSystemPrompt: ["APPEND"]),
            stream: (_, context, _, _) => { observed = context; return ModeTestRuntime.StreamText("ok"); });

        await runtime.Harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.StartsWith("CUSTOM", observed.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("APPEND", observed.SystemPrompt);
    }

    [Fact]
    public async Task LoadedSkillsAreIncludedByPromptComposer()
    {
        AgentContext? observed = null;
        var skills = new[]
        {
            new Skill("example", "Example skill", "Example content", "/repo/skills/example/SKILL.md"),
            new Skill("hidden", "Hidden skill", "Hidden content", "/repo/skills/hidden/SKILL.md", DisableModelInvocation: true)
        };
        var runtime = await ModeTestRuntime.CreateAsync(
            skills: skills,
            stream: (_, context, _, _) =>
            {
                observed = context;
                return ModeTestRuntime.StreamText("ok");
            });

        await runtime.Harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.Contains("<available_skills>", observed.SystemPrompt);
        Assert.Contains("<name>example</name>", observed.SystemPrompt);
        Assert.DoesNotContain("<name>hidden</name>", observed.SystemPrompt);
    }

    [Fact]
    public async Task BootstrapIncludesActiveToolPromptSnippetsOnly()
    {
        AgentContext? observed = null;
        var runtime = await ModeTestRuntime.CreateAsync(
            args: new CliArgs(Tools: ["read"]),
            stream: (_, context, _, _) => { observed = context; return ModeTestRuntime.StreamText("ok"); });

        await runtime.Harness.PromptAsync("hello");

        Assert.Contains("- read: Read file contents", observed!.SystemPrompt);
        Assert.DoesNotContain("- bash:", observed.SystemPrompt);
    }

    [Fact]
    public async Task PromptTemplateSlashCommandExpandsAndSubmitsTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "prompts"));
        await File.WriteAllTextAsync(Path.Combine(root, "prompts", "release.md"), "---\ndescription: Release notes\n---\nRelease $1 $ARGUMENTS");
        var runtime = await ModeTestRuntime.CreateAsync(cwd: root, args: new CliArgs(PromptTemplates: ["./prompts"]));
        var commands = SlashCommandRegistryFactory.Create(runtime);
        string? submitted = null;
        var context = new SlashCommandContext(
            "prompt:release",
            runtime,
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            SubmitPromptAsync: (text, _) => { submitted = text; return Task.CompletedTask; });

        var result = await commands.ExecuteAsync("/prompt:release 1.2.3 stable", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("Release 1.2.3 1.2.3 stable", submitted);
        Assert.Contains(commands.Commands, command => command.Name == "prompt:release" && command.SourceId == "prompt-template");
    }
}
