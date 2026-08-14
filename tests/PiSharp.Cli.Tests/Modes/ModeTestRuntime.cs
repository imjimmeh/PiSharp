using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Agent.Sessions;
using PiSharp.Cli.Parsing;
using PiSharp.Extensions;
using PiSharp.Runtime;
using PiSharp.Runtime.IO;
using PiSharp.Compatibility.Resources;
using PiSharp.Compatibility.Settings;
using PiSharp.Tools;
using Microsoft.Extensions.Logging;

namespace PiSharp.Cli.Tests.Modes;

internal static class ModeTestRuntime
{
    public static async Task<SessionRuntime> CreateAsync(AgentStreamAsync? stream = null, CliArgs? args = null, string? cwd = null, IReadOnlyList<Skill>? skills = null, ExtensionManager? extensionManager = null, ILoggerFactory? loggerFactory = null)
    {
        var root = cwd ?? Path.Combine(Path.GetTempPath(), "pisharp-mode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var env = new SystemExecutionEnv(root);
        var home = Path.Combine(Path.GetTempPath(), "pisharp-mode-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var effectiveArgs = args ?? new CliArgs();
        var settings = await new PiSettingsStore().LoadAsync(root, home);
        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(
            settings,
            root,
            effectiveArgs.Extensions ?? [],
            effectiveArgs.Skills ?? [],
            effectiveArgs.PromptTemplates ?? [],
            effectiveArgs.Themes ?? [],
            effectiveArgs.NoExtensions,
            effectiveArgs.NoSkills,
            effectiveArgs.NoPromptTemplates,
            effectiveArgs.NoThemes,
            effectiveArgs.NoContextFiles,
            effectiveArgs.NoTsExtensions));
        var tools = RuntimeToolSelector.Create(env, new RuntimeToolOptions(effectiveArgs.Tools, effectiveArgs.NoTools, effectiveArgs.NoBuiltinTools));
        var loadedSkills = skills ?? [];
        var (promptTemplateCatalog, _) = await PromptTemplateCatalog.LoadAsync(env, resources.PromptTemplatePaths);
        var systemPromptOptions = new SystemPromptBuildOptions(
            Cwd: root,
            Tools: tools.Tools.Select(tool => new PiSharp.Agent.Resources.ToolPromptInfo(tool.Name, tool.PromptSnippet, tool.PromptGuidelines)).ToArray(),
            SelectedToolNames: tools.ActiveToolNames ?? tools.Tools.Select(tool => tool.Name).ToArray(),
            CustomPrompt: effectiveArgs.SystemPrompt ?? resources.SystemPrompt,
            AppendPrompt: effectiveArgs.AppendSystemPrompt is { Count: > 0 } ? string.Join("\n\n", effectiveArgs.AppendSystemPrompt) : string.Join("\n\n", resources.AppendSystemPrompts ?? []),
            ContextFiles: effectiveArgs.NoContextFiles ? [] : ToSystemPromptContextFiles(resources.ContextFiles),
            Skills: loadedSkills,
            ReadmePath: Path.GetFullPath("README.md", root),
            DocsPath: Path.GetFullPath("docs", root),
            ExamplesPath: Path.GetFullPath("examples", root));
        var repo = new JsonlSessionRepo(env, "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var systemPromptContext = SystemPromptBuildOptionsMapper.ToContext(systemPromptOptions);
        var systemPromptComposer = SystemPromptComposer.CreateDefault();
        return new SessionRuntime(repo, createOptions, session => Harness(session, stream ?? FakeStream("ok"), tools.Tools, tools.ActiveToolNames, systemPromptOptions, loadedSkills, systemPromptContext, systemPromptComposer), initial, extensionManager: extensionManager, resources: resources, systemPromptOptions: systemPromptOptions, skills: loadedSkills, promptTemplates: promptTemplateCatalog, loggerFactory: loggerFactory);
    }

    private static IReadOnlyList<PiSharp.Agent.Resources.SystemPromptContextFile> ToSystemPromptContextFiles(IReadOnlyList<PiResourceContextFile>? contextFiles)
        => contextFiles?.Select(file => new PiSharp.Agent.Resources.SystemPromptContextFile(file.Path, file.Content)).ToArray() ?? [];

    public static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session, AgentStreamAsync stream)
        => Harness(session, stream, [], null, null);

    private static AgentHarness<JsonlSessionMetadata> Harness(
        ISession<JsonlSessionMetadata> session,
        AgentStreamAsync stream,
        IReadOnlyList<PiSharp.Agent.Core.Tools.IAgentTool> tools,
        IReadOnlyList<string>? activeToolNames,
        SystemPromptBuildOptions? systemPromptOptions,
        IReadOnlyList<Skill>? skills = null,
        SystemPromptCompositionContext? systemPromptContext = null,
        ISystemPromptComposer? systemPromptComposer = null)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, tools, activeToolNames, systemPromptOptions, SystemPromptContext: systemPromptContext, SystemPromptComposer: systemPromptComposer, Skills: skills?.ToArray()));

    public static AgentStreamAsync FakeStream(string text)
    {
        AgentStreamAsync stream = (_, _, _, _) => StreamHelper(text);
        return stream;
    }

    public static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    public static async IAsyncEnumerable<AssistantMessageEvent> StreamText(string text)
    {
        await foreach (var item in StreamHelper(text)) yield return item;
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamHelper(string text)
    {
        await Task.Yield();
        var message = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}
