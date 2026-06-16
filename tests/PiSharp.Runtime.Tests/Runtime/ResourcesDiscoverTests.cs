using System.Runtime.CompilerServices;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.TsBridge;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

public sealed class ResourcesDiscoverTests
{
    [Fact]
    public async Task NativeExtensionResourcesDiscoverContributesPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-disc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.ResourcesDiscover, (evt, _) =>
        {
            evt.AddResourcesDiscoverPaths(skillPaths: [Path.Combine(root, "skills")]);
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionManager: manager);

        var result = await runtime.DispatchResourcesDiscoverAsync(CancellationToken.None);

        Assert.Contains(result.SkillPaths, path => path == Path.Combine(root, "skills"));
    }

    [Fact]
    public async Task ExtensionHandlerFailureDoesNotPreventLaterContributions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-disc-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:throw", ExtensionEventNames.ResourcesDiscover, (evt, _) =>
        {
            throw new InvalidOperationException("This handler failed");
        });
        registry.RegisterHandler("extension:ok", ExtensionEventNames.ResourcesDiscover, (evt, _) =>
        {
            evt.AddResourcesDiscoverPaths(skillPaths: [Path.Combine(root, "skills")]);
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionManager: manager);

        var result = await runtime.DispatchResourcesDiscoverAsync(CancellationToken.None);

        Assert.Contains(result.SkillPaths, path => path == Path.Combine(root, "skills"));
    }

    [Fact]
    public async Task ResourcesDiscoverReturnsEmptyResultWhenNoHandlers()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-disc-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session);

        var result = await runtime.DispatchResourcesDiscoverAsync(CancellationToken.None);

        Assert.Empty(result.SkillPaths);
        Assert.Empty(result.PromptPaths);
        Assert.Empty(result.ThemePaths);
    }

    [Fact]
    public async Task ResourcesDiscoverSubscribesToCorrectEventName()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-disc-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.ResourcesDiscover, (evt, _) =>
        {
            evt.AddResourcesDiscoverPaths(skillPaths: ["/tmp/skills"]);
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);

        var evt = new ExtensionEvent(ExtensionEventNames.ResourcesDiscover, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), new object());
        var dispatched = false;
        foreach (var handler in registry.HandlersFor(ExtensionEventNames.ResourcesDiscover))
        {
            await handler.Value.Handler(evt, CancellationToken.None);
            dispatched = true;
        }

        Assert.True(dispatched);
        Assert.NotNull(evt.ResourcesDiscoverResult);
        Assert.Contains("/tmp/skills", evt.ResourcesDiscoverResult.SkillPaths);
    }

    [Fact]
    public async Task BootstrapResourcesDiscoverContributedPromptPathsRebuildsPromptCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rt-disc-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var contributedPromptFile = Path.Combine(root, "contributed-release.md");
        await File.WriteAllTextAsync(contributedPromptFile, "---\ndescription: Contributed release notes\n---\nRelease $1");
        var escapedFile = contributedPromptFile.Replace("\\", "\\\\");
        var extensionPath = Path.Combine(root, ".pi", "extensions", "prompt-contributor.js");
        await File.WriteAllTextAsync(extensionPath, $$"""
            export default function activate(pi) {
              pi.on("resources_discover", () => ({
                promptPaths: ["{{escapedFile}}"]
              }));
            }
            """);
        var env = new SystemExecutionEnv(root);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true)));

        Assert.Contains(runtime.PromptTemplates.Templates, template => template.Name == "contributed-release" && template.Description == "Contributed release notes");
        Assert.Equal("Release 1.2.3", runtime.PromptTemplates.FormatInvocation("contributed-release", ["1.2.3"]));
    }

    [Fact]
    public async Task BootstrapResourcesDiscoverContributedThemePathReloadsThemeDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rt-disc-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var contributedThemeFile = Path.Combine(root, "contributed-theme.json");
        await File.WriteAllTextAsync(contributedThemeFile, "{\"name\":\"Contributed Theme\",\"tokens\":{\"accent\":\"#cccccc\"}}");
        var escapedFile = contributedThemeFile.Replace("\\", "\\\\");
        var extensionPath = Path.Combine(root, ".pi", "extensions", "theme-contributor.js");
        await File.WriteAllTextAsync(extensionPath, $$"""
            export default function activate(pi) {
              pi.on("resources_discover", () => ({
                themePaths: ["{{escapedFile}}"]
              }));
            }
            """);
        var env = new SystemExecutionEnv(root);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true)));

        Assert.NotNull(runtime.Theme);
        Assert.Equal("Contributed Theme", runtime.Theme!.Name);
    }

    [Fact]
    public async Task DispatchResourcesDiscoverPropagatesCancellationWhenTokenIsCanceled()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-disc-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.ResourcesDiscover, (evt, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionManager: manager);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runtime.DispatchResourcesDiscoverAsync(cts.Token));
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}
