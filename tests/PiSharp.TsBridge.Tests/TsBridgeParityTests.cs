using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Extensions;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class TsBridgeParityTests
{
    [Fact]
    public async Task HostLoadsExtensionModuleAndRegistersCommand()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('schedule-prompt', { description: 'Schedule prompt', handler: () => 'scheduled' }); }");
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "schedule-prompt");
    }

    [Fact]
    public async Task HostLoadsTypeScriptExtensionWithTypeOnlySyntax()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-types-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "type ExtensionAPI = { registerCommand(name: string, options: { description: string, handler: () => string }): void }; export default function activate(pi: ExtensionAPI) { pi.registerCommand('typed-command', { description: 'Typed command', handler: () => 'typed' }); }");
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "typed-command");
    }

    [Fact]
    public async Task HostReportsClearErrorWhenTypeScriptCompilerIsUnavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-missing-compiler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any): void { pi.registerCommand('missing-compiler-command', { description: 'Typed command', handler: () => 'typed' }); }");
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        using var home = TemporaryUserHome(Path.Combine(dir, "home-without-managed-typescript"));
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("TypeScript compiler is required", result.Error);
    }

    [Fact]
    public async Task HostLoadsTypeScriptExtensionUsingExtensionLocalCompiler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-local-compiler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any): void { pi.registerCommand('local-compiler-command', { description: 'Typed command', handler: () => 'typed' }); }");
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "local-compiler-command");
    }

    [Fact]
    public async Task ManagedExtensionCanReuseSiblingManagedTypeScriptCompilerAfterNonManagedLoad()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "pisharp-ts-managed-home-" + Guid.NewGuid().ToString("N"));
        var managedNodeModules = Path.Combine(tempHome, ".pi", "agent", "npm", "node_modules");
        var compilerPackage = Path.Combine(managedNodeModules, "compiler-holder");
        var managedPackage = Path.Combine(managedNodeModules, "managed-extension");
        var nonManagedDir = Path.Combine(Path.GetTempPath(), "pisharp-ts-non-managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(managedPackage);
        Directory.CreateDirectory(nonManagedDir);
        await InstallFakeTypeScriptCompilerAsync(compilerPackage);
        var nonManagedPath = Path.Combine(nonManagedDir, "extension.ts");
        await File.WriteAllTextAsync(nonManagedPath, "// @ts-nocheck\nexport default function activate(pi) { pi.registerCommand('non-managed-first', { description: 'Non-managed command', handler: () => 'ok' }); }");
        var managedPath = Path.Combine(managedPackage, "index.ts");
        await File.WriteAllTextAsync(managedPath, "export default function activate(pi: any): void { pi.registerCommand('managed-sibling-compiler', { description: 'Managed command', handler: () => 'ok' }); }");
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(nonManagedDir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: nonManagedDir), registry, binding);
        var home = TemporaryUserHome(tempHome);
        try
        {
            await host.StartAsync(CancellationToken.None);
        }
        finally
        {
            home.Dispose();
        }

        var nonManaged = await host.LoadAsync(nonManagedPath, binding, CancellationToken.None);
        var managed = await host.LoadAsync(managedPath, binding, CancellationToken.None);

        Assert.True(nonManaged.Ok, nonManaged.Error);
        Assert.True(managed.Ok, managed.Error);
        Assert.Contains(registry.Commands, command => command.Value.Name == "non-managed-first");
        Assert.Contains(registry.Commands, command => command.Value.Name == "managed-sibling-compiler");
    }

    [Fact]
    public async Task HostProvidesPiCodingAgentCompatibilityModule()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-pi-agent-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { defineTool, formatSize, getAgentDir, isToolCallEventType } from '@earendil-works/pi-coding-agent';
            export default function activate(pi: any): void {
              const tool = defineTool({ name: 'shim_tool' });
              const ok = isToolCallEventType('read', { toolName: 'read' });
              pi.registerCommand(`shim-${tool.name}-${ok}-${formatSize(1024)}-${getAgentDir().includes('.pi')}`, { description: 'Shim command', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "shim-shim_tool-true-1.0 KB-true");
    }

    [Fact]
    public async Task HostAliasesLegacyAndSplitPiRuntimePackages()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-runtime-package-aliases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { defineTool, getAgentDir } from '@mariozechner/pi-coding-agent';
            import { Text } from '@mariozechner/pi-tui';
            import { StringEnum as OldStringEnum } from '@mariozechner/pi-ai';
            import { StringEnum, Type } from '@earendil-works/pi-ai';
            export default function activate(pi: any): void {
              const tool = defineTool({ name: 'legacy_tool' });
              const line = new Text('legacy-text', 0, 0).render(80)[0];
              const oldEnum = OldStringEnum(['old-value']).enum[0];
              const schema = Type.Object({ value: Type.String(), mode: StringEnum(['new-value']) });
              pi.registerCommand(`alias-${tool.name}-${line}-${oldEnum}-${schema.type}-${schema.properties.value.type}-${getAgentDir().includes('.pi')}`, { description: 'Alias command', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "alias-legacy_tool-legacy-text-old-value-object-string-true");
    }

    [Fact]
    public async Task HostProvidesPiCodingAgentSessionManagerCompatibilityExport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-pi-agent-session-manager-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { SessionManager } from '@earendil-works/pi-coding-agent';
            export default function activate(pi: any): void {
              const ok = typeof SessionManager === 'function';
              pi.registerCommand(`shim-session-manager-${ok}`, { description: 'Shim command', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "shim-session-manager-true");
    }

    [Fact]
    public async Task HostProvidesPiCodingAgentParseFrontmatterCompatibilityShape()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-pi-agent-frontmatter-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { parseFrontmatter } from '@earendil-works/pi-coding-agent';
            export default function activate(pi: any): void {
              const parsed = parseFrontmatter(`---
            display_name: Fancy Agent
            description: Agent description
            max_turns: 3
            enabled: false
            ---
            Body text
            `);
              pi.registerCommand(`frontmatter-${parsed.frontmatter.display_name}-${parsed.frontmatter.description}-${parsed.frontmatter.max_turns}-${parsed.frontmatter.enabled}-${parsed.body.trim()}`, { description: 'Shim command', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "frontmatter-Fancy Agent-Agent description-3-false-Body text");
    }

    [Fact]
    public async Task CachedTypeScriptModuleSeesUpdatedPiCodingAgentCompatibilityShim()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-stale-frontmatter-shim-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { parseFrontmatter } from '@earendil-works/pi-coding-agent';
            export default function activate(pi: any): void {
              const parsed = parseFrontmatter(`---
            display_name: Cached Agent
            ---
            Body
            `);
              pi.registerCommand(`cached-frontmatter-${parsed.frontmatter.display_name}`, { description: 'Shim command', handler: () => 'ok' });
            }
            """);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok, loaded.Error);
        }
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "pisharp-pi-coding-agent-shim.mjs"), """
            export function parseFrontmatter(text) { return { data: {}, content: String(text ?? '') }; }
            """);
        var registry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry, binding);
        await secondHost.StartAsync(CancellationToken.None);

        var result = await secondHost.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(registry.Commands, command => command.Value.Name == "cached-frontmatter-Cached Agent");
    }

    [Fact]
    public async Task BackgroundBatchActivationLoadsMultipleReplayedDescriptors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-background-batch-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        var extensionPaths = Enumerable.Range(0, 3)
            .Select(index => Path.Combine(dir, $"extension-{index}.mjs"))
            .ToArray();
        for (var i = 0; i < extensionPaths.Length; i++)
        {
            await File.WriteAllTextAsync(extensionPaths[i], $$"""
                export default function activate(pi) {
                  pi.registerCommand('background_batch_{{i}}', { description: 'background batch {{i}}', handler: () => 'ok' });
                }
                """);
        }
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            var cold = await firstHost.LoadManyAsync(extensionPaths, binding, CancellationToken.None);
            Assert.True(cold.Ok);
            Assert.All(cold.Results!, result => Assert.True(result.Ok, result.Error));
        }
        var registry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry, binding);
        foreach (var extensionPath in extensionPaths)
            Assert.True(await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None));

        var results = await secondHost.ActivateExtensionsInBackgroundAsync(extensionPaths, binding, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Ok, result.Error));
        for (var i = 0; i < extensionPaths.Length; i++)
            Assert.Contains(registry.Commands, command => command.Value.Name == $"background_batch_{i}");
    }

    [Fact]
    public async Task BackgroundBatchActivationPollsStatusesInBatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-background-batch-status-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        var extensionPaths = Enumerable.Range(0, 6)
            .Select(index => Path.Combine(dir, $"extension-{index}.mjs"))
            .ToArray();
        for (var i = 0; i < extensionPaths.Length; i++)
        {
            await File.WriteAllTextAsync(extensionPaths[i], $$"""
                export default async function activate(pi) {
                  await new Promise(resolve => setTimeout(resolve, 800));
                  pi.registerCommand('background_batch_status_{{i}}', { description: 'background batch status {{i}}', handler: () => 'ok' });
                }
                """);
        }
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            var cold = await firstHost.LoadManyAsync(extensionPaths, binding, CancellationToken.None);
            Assert.True(cold.Ok);
            Assert.All(cold.Results!, result => Assert.True(result.Ok, result.Error));
        }
        using var loggerFactory = new RecordingLoggerFactory();
        var registry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry, binding, loggerFactory);
        foreach (var extensionPath in extensionPaths)
            Assert.True(await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None));

        var results = await secondHost.ActivateExtensionsInBackgroundAsync(extensionPaths, binding, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Ok, result.Error));
        Assert.DoesNotContain(loggerFactory.Messages, message => message.Contains("method=background_load_status", StringComparison.Ordinal) && !message.Contains("method=background_load_statuses", StringComparison.Ordinal));
        Assert.Contains(loggerFactory.Messages, message => message.Contains("method=background_load_statuses", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeScriptExtensionContextExposesSessionManagerSessionId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-session-id-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on('session_shutdown', (_event, ctx) => {
                const sessionId = ctx.sessionManager.getSessionId();
                if (sessionId !== 'session-42') throw new Error(`expected session-42, got ${sessionId}`);
              });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            GetSessionIdAsync = _ => Task.FromResult<string?>("session-42")
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);
        var result = await host.LoadAsync(extensionPath, binding, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        var evt = new ExtensionEvent(ExtensionEventNames.SessionShutdown, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionShutdown("dispose")), new ExtensionSessionShutdownEvent("dispose"));

        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);
    }

    [Fact]
    public async Task TypeScriptEventsOnReturnsCallableUnsubscribe()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              const unsubscribe = pi.events.on('subagents:rpc:ping', () => {});
              if (typeof unsubscribe !== 'function') throw new Error('unsubscribe is not callable');
              unsubscribe();
              pi.registerCommand('event-unsubscribe-callable', { description: 'ok', handler: () => 'ok' });
            }
            """);

        Assert.Contains(registry.Commands, command => command.Value.Name == "event-unsubscribe-callable");
    }

    [Fact]
    public async Task TypeScriptSessionShutdownHandlerFailuresDoNotCrashHost()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.on('session_shutdown', () => { throw new Error('this.unsubscribe is not a function'); });
            }
            """);
        var evt = new ExtensionEvent(ExtensionEventNames.SessionShutdown, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionShutdown("dispose")), new ExtensionSessionShutdownEvent("dispose"));

        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);
    }

    [Fact]
    public async Task TsBridgeRunnerContinuesAfterMalformedJsonInputLine()
    {
        var options = new TsBridgeOptions();
        var runnerPath = options.EffectiveRunnerPath(AppContext.BaseDirectory);
        var process = Process.Start(new ProcessStartInfo(options.NodeExecutable, runnerPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        });

        Assert.NotNull(process);
        try
        {
            await process!.StandardInput.WriteLineAsync("{");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"initialize\",\"params\":{\"extensionPaths\":[]}}");
            await process.StandardInput.FlushAsync();

            JsonDocument? initializeResponse = null;
            for (var attempts = 0; attempts < 10; attempts++)
            {
                var line = await ReadLineWithTimeoutAsync(process.StandardOutput, TimeSpan.FromSeconds(2));
                Assert.False(string.IsNullOrWhiteSpace(line));
                using var parsed = JsonDocument.Parse(line!);
                if (!parsed.RootElement.TryGetProperty("id", out var id) || id.GetString() != "1")
                {
                    continue;
                }

                initializeResponse = JsonDocument.Parse(line!);
                break;
            }

            Assert.NotNull(initializeResponse);
            Assert.True(initializeResponse!.RootElement.TryGetProperty("result", out var result));
            Assert.True(result.GetProperty("ok").GetBoolean());
        }
        finally
        {
            if (!process!.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            process.Dispose();
        }
    }

    [Fact]
    public async Task TypeScriptTranspileCacheRecoversFromStaleBarePiCodingAgentImport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-stale-shim-cache-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { defineTool } from '@earendil-works/pi-coding-agent';
            export default function activate(pi: any): void {
              const tool = defineTool({ name: 'stale_shim_tool' });
              pi.registerCommand(`stale-shim-${tool.name}`, { description: 'Shim command', handler: () => 'ok' });
            }
            """);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok, loaded.Error);
        }
        var cachedModule = Assert.Single(Directory.EnumerateFiles(cacheDir, "*.mjs", SearchOption.AllDirectories)
, file => !Path.GetFileName(file).StartsWith("pisharp-pi-coding-agent-shim", StringComparison.Ordinal) && File.ReadAllText(file).Contains("stale-shim-", StringComparison.Ordinal));
        var cachedSource = await File.ReadAllTextAsync(cachedModule);
        Assert.Contains("pisharp-pi-coding-agent-shim.", cachedSource);
        await File.WriteAllTextAsync(cachedModule, cachedSource.Replace(ShimFileUrlPattern(cachedSource), "'@earendil-works/pi-coding-agent'"));
        var registry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry, binding);
        await secondHost.StartAsync(CancellationToken.None);

        var result = await secondHost.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(registry.Commands, command => command.Value.Name == "stale-shim-stale_shim_tool");
    }

    [Fact]
    public async Task TypeScriptTranspileCacheRefreshesCachedModulesWithStaleCompatibilityShimUrl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-stale-shim-url-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              const { session } = await createAgentSession();
              const result = await session.prompt('task');
              pi.registerCommand(`fresh-shim-${session.sessionId === 'child-1'}-${result.sessionId === 'child-1'}`, { description: 'Fresh shim URL check', handler: () => 'ok' });
            }
            """);
        const string sessionId = "child-1";
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) => Task.FromResult<object?>(new
            {
                ok = true,
                sessionId,
                session = new { sessionId, messages = Array.Empty<object>(), entries = Array.Empty<object>() },
                extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                modelFallbackMessage = (string?)null
            }),
            AgentSessionPromptAsync = (_, _, _, _) => Task.FromResult<object?>(new
            {
                ok = true,
                sessionId,
                session = new { sessionId, messages = Array.Empty<object>(), entries = Array.Empty<object>() }
            })
        };
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok, loaded.Error);
        }

        var cachedModule = Assert.Single(Directory.EnumerateFiles(cacheDir, "*.mjs", SearchOption.AllDirectories)
, file => !Path.GetFileName(file).StartsWith("pisharp-pi-coding-agent-shim", StringComparison.Ordinal) && File.ReadAllText(file).Contains("fresh-shim-", StringComparison.Ordinal));
        var cachedSource = await File.ReadAllTextAsync(cachedModule);
        Assert.Contains("pisharp-pi-coding-agent-shim.", cachedSource, StringComparison.Ordinal);
        var staleShimPath = Path.Combine(cacheDir, "pisharp-pi-coding-agent-shim.stale.mjs");
        await File.WriteAllTextAsync(staleShimPath, """
            export async function createAgentSession() {
              return {
                session: {
                  get sessionId() { return undefined; },
                  async prompt() { return { sessionId: undefined }; }
                }
              };
            }
            """);
        var staleShimSpecifier = "'" + new Uri(staleShimPath).AbsoluteUri + "'";
        await File.WriteAllTextAsync(cachedModule, cachedSource.Replace(ShimFileUrlPattern(cachedSource), staleShimSpecifier));

        var registry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry, binding);
        await secondHost.StartAsync(CancellationToken.None);

        var result = await secondHost.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(registry.Commands, command => command.Value.Name == "fresh-shim-true-true");
    }

    [Fact]
    public async Task TypeScriptTranspileCacheRecoversFromMalformedCachedJavaScript()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-malformed-cache-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any): void { pi.registerCommand('malformed-cache-ok', { description: 'Recovered command', handler: () => 'ok' }); }");
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok, loaded.Error);
        }
        var cachedModule = Assert.Single(Directory.EnumerateFiles(cacheDir, "*.mjs", SearchOption.AllDirectories)
, file => File.ReadAllText(file).Contains("malformed-cache-ok", StringComparison.Ordinal));
        await File.WriteAllTextAsync(cachedModule, "import { , broken } from 'node:fs';\nexport default function activate(pi) { pi.registerCommand('wrong-cache', { description: 'Broken cache', handler: () => 'no' }); }\n");
        var registry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry, binding);
        await secondHost.StartAsync(CancellationToken.None);

        var result = await secondHost.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(registry.Commands, command => command.Value.Name == "malformed-cache-ok");
    }

    [Fact]
    public async Task TypeScriptImportRewriteDoesNotMutateStringLiteralText()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-import-rewrite-literal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { defineTool } from '@earendil-works/pi-coding-agent';
            export default function activate(pi: any): void {
              const literal = "'@earendil-works/pi-coding-agent'";
              const tool = defineTool({ name: 'rewrite_check' });
              pi.registerCommand(`literal-${literal}-${tool.name}`, { description: 'Import rewrite literal check', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "literal-'@earendil-works/pi-coding-agent'-rewrite_check");
    }

    [Fact]
    public async Task StartCancellationTokenDoesNotOwnBridgePumpLifetime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-pump-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('after-start-token-cancelled', { description: 'ok', handler: () => 'ok' }); }");
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        using var startupCts = new CancellationTokenSource();
        await host.StartAsync(startupCts.Token);

        await startupCts.CancelAsync();
        var result = await host.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(registry.Commands, command => command.Value.Name == "after-start-token-cancelled");
    }

    [Fact]
    public async Task RegisteredTypeScriptCommandInvokesNodeHandler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "marker.txt").Replace("\\", "\\\\");
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, $$"""import fs from 'node:fs/promises'; export default function activate(pi) { pi.registerCommand('schedule-prompt', { description: 'Schedule prompt', handler: async (args) => { await fs.writeFile('{{marker}}', args); } }); }""");
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);
        await host.StartAsync(CancellationToken.None);

        var command = Assert.Single(registry.Commands, item => item.Value.Name == "schedule-prompt").Value;
        await command.Handler("tomorrow", CancellationToken.None);

        Assert.Equal("tomorrow", await File.ReadAllTextAsync(Path.Combine(dir, "marker.txt")));
    }

    [Fact]
    public async Task TypeScriptExtensionsCanUseRootSessionMetadataAliases()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-session-aliases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              if (pi.getSessionName() !== 'Initial Session') throw new Error('getSessionName alias failed');
              pi.setSessionName('Updated Session');
              pi.setLabel('entry-1', 'checkpoint');
              pi.registerCommand('session-aliases-ok', { description: 'ok', handler: () => 'ok' });
            }
            """);
        var sessionNames = new List<string>();
        var labels = new List<(string EntryId, string? Label)>();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            GetSessionSnapshotAsync = _ => Task.FromResult<object?>(new { sessionName = "Initial Session" }),
            SetSessionNameAsync = (name, _) => { sessionNames.Add(name); return Task.CompletedTask; },
            SetLabelAsync = (entryId, label, _) => { labels.Add((entryId, label)); return Task.CompletedTask; }
        };
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "session-aliases-ok");
        Assert.Equal(["Updated Session"], sessionNames);
        Assert.Equal(("entry-1", "checkpoint"), Assert.Single(labels));
    }

    [Fact]
    public async Task TypeScriptCommandContextSupportsSessionReplacementCallbacks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-command-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.registerCommand('session-api', {
                description: 'Session API',
                handler: async (_args, ctx) => {
                  if (typeof ctx.waitForIdle !== 'function') throw new Error('waitForIdle missing');
                  if (typeof ctx.newSession !== 'function') throw new Error('newSession missing');
                  await ctx.waitForIdle();
                  if (ctx.sessionManager.getBranch().length !== 1) throw new Error('branch snapshot missing');
                  const result = await ctx.newSession({
                    withSession: async replacement => {
                      if (typeof replacement.sendUserMessage !== 'function') throw new Error('replacement sendUserMessage missing');
                      await replacement.sendUserMessage('hello replacement');
                    }
                  });
                  if (result.cancelled) throw new Error('newSession cancelled');
                }
              });
            }
            """);
        var messages = new List<string>();
        var binding = new ExtensionRuntimeBinding(dir, true, NoExtensionUi.Instance)
        {
            WaitForIdleAsync = _ => Task.CompletedTask,
            GetSessionSnapshotAsync = _ => Task.FromResult<object?>(new { branch = new object[] { new { type = "message" } }, entries = Array.Empty<object>(), sessionId = "session" }),
            NewSessionAsync = _ => Task.FromResult(new ExtensionSessionReplacementResult(false, SessionId: "replacement-session")),
            SendUserMessageAsync = (content, _, _) => { messages.Add(content); return Task.CompletedTask; }
        };
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var command = Assert.Single(registry.Commands, item => item.Value.Name == "session-api").Value;
        await command.Handler(string.Empty, CancellationToken.None);

        Assert.Equal(["hello replacement"], messages);
    }

    [Fact]
    public async Task SendUserMessageReturnsSessionSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-send-user-message-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.registerCommand('send-user-message-snapshot', {
                description: 'Verify sendUserMessage returns snapshot',
                handler: async (_args, ctx) => {
                  await ctx.waitForIdle();
                  let sendResult = null;
                  await ctx.newSession({
                    withSession: async replacement => {
                      sendResult = await replacement.sendUserMessage('hello snapshot test');
                    }
                  });
                  if (!sendResult || !(sendResult.session || sendResult.value?.session)) throw new Error('MISSING_SESSION:' + JSON.stringify(sendResult));
                  const branch = (sendResult.session || sendResult.value?.session)?.branch;
                  if (!branch || branch.length === 0) throw new Error('MISSING_BRANCH:' + JSON.stringify(branch));
                }
              });
            }
            """);
        var messages = new List<string>();
        var snapshotCallCount = 0;
        var binding = new ExtensionRuntimeBinding(dir, true, NoExtensionUi.Instance)
        {
            WaitForIdleAsync = _ => Task.CompletedTask,
            GetSessionSnapshotAsync = _ =>
            {
                Interlocked.Increment(ref snapshotCallCount);
                return Task.FromResult<object?>(new { branch = new object[] { new { type = "message", role = "assistant" } }, entries = Array.Empty<object>(), sessionId = "session" });
            },
            NewSessionAsync = _ => Task.FromResult(new ExtensionSessionReplacementResult(false, SessionId: "replacement-session")),
            SendUserMessageAsync = (content, _, _) => { messages.Add(content); return Task.CompletedTask; }
        };
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var command = Assert.Single(registry.Commands, item => item.Value.Name == "send-user-message-snapshot").Value;
        await command.Handler(string.Empty, CancellationToken.None);

        Assert.Single(messages);
        Assert.Equal("hello snapshot test", messages[0]);
        Assert.True(snapshotCallCount > 0, "SendUserMessageAsync should return a session snapshot");
    }

    [Fact]
    public async Task ReplacementSendUserMessageRefreshesSessionManagerSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-replacement-send-user-message-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.registerCommand('replacement-send-user-message-snapshot', {
                description: 'Verify replacement sendUserMessage refreshes sessionManager snapshot',
                handler: async (_args, ctx) => {
                  await ctx.newSession({
                    withSession: async replacement => {
                      await replacement.sendUserMessage('hello replacement snapshot');
                    }
                  });
                  const branch = ctx.sessionManager.getBranch();
                  if (!branch.some(entry => entry.role === 'assistant')) throw new Error('STALE_BRANCH:' + JSON.stringify(branch));
                }
              });
            }
            """);
        var messages = new List<string>();
        var newSessionStarted = false;
        var userMessageSent = false;
        var binding = new ExtensionRuntimeBinding(dir, true, NoExtensionUi.Instance)
        {
            GetSessionSnapshotAsync = _ => Task.FromResult<object?>(userMessageSent
                ? new { branch = new object[] { new { type = "message", role = "assistant" } }, entries = Array.Empty<object>(), sessionId = "replacement-session" }
                : new { branch = Array.Empty<object>(), entries = Array.Empty<object>(), sessionId = newSessionStarted ? "replacement-session" : "initial-session" }),
            NewSessionAsync = _ =>
            {
                newSessionStarted = true;
                return Task.FromResult(new ExtensionSessionReplacementResult(false, SessionId: "replacement-session"));
            },
            SendUserMessageAsync = (content, _, _) =>
            {
                messages.Add(content);
                userMessageSent = true;
                return Task.CompletedTask;
            }
        };
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var command = Assert.Single(registry.Commands, item => item.Value.Name == "replacement-send-user-message-snapshot").Value;
        await command.Handler(string.Empty, CancellationToken.None);

        Assert.Equal(["hello replacement snapshot"], messages);
    }

    [Fact]
    public async Task RegisteredTypeScriptToolReceivesDocumentedExecuteArguments()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerTool({
                name: 'greet',
                label: 'Greet',
                description: 'Greet someone by name',
                parameters: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'] },
                execute: async (toolCallId, params, signal, onUpdate, ctx) => ({
                  content: [{ type: 'text', text: `${toolCallId}:${params?.name}:${ctx?.extensionId}` }],
                  details: { aborted: signal?.aborted ?? null, hasUpdate: typeof onUpdate === 'function' }
                })
              });
            }
            """);
        var tool = Assert.Single(registry.Tools).Value;
        using var args = JsonDocument.Parse("{\"name\":\"Ada\"}");

        var result = await tool.ExecuteAsync("tool-42", args.RootElement.Clone(), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.StartsWith("tool-42:Ada:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredTypeScriptToolCanRenderCallAndResult()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerTool({
                name: 'styled_todo',
                label: 'Styled Todo',
                description: 'Styled todo tool',
                parameters: { type: 'object', properties: { subject: { type: 'string' } } },
                execute: async (_toolCallId, params) => ({
                  content: [{ type: 'text', text: `Created: ${params.subject}` }],
                  details: { status: 'pending' }
                }),
                renderCall: (args) => ({ render: () => [`todo + ${args.subject}`] }),
                renderResult: (result) => ({ render: () => [`○ ${result.details.status}`] })
              });
            }
            """);
        var tool = Assert.Single(registry.Tools).Value;
        var renderer = Assert.IsAssignableFrom<IAgentToolRenderer>(tool);
        using var args = JsonDocument.Parse("{\"subject\":\"Audit extension styling\"}");
        var executeResult = await tool.ExecuteAsync("tool-42", args.RootElement.Clone(), CancellationToken.None);

        var call = await renderer.RenderCallAsync(new ToolRenderRequest("tool-42", tool.Name, args.RootElement.Clone(), null, false, false, false, 80), CancellationToken.None);
        var result = await renderer.RenderResultAsync(new ToolRenderRequest("tool-42", tool.Name, args.RootElement.Clone(), executeResult, false, false, false, 80), CancellationToken.None);

        Assert.Equal(["todo + Audit extension styling"], call?.Lines);
        Assert.Equal(["○ pending"], result?.Lines);
    }

    [Fact]
    public async Task TypeScriptExtensionSessionStartCanRegisterTool()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-session-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on('session_start', (_event, ctx) => {
                pi.registerTool({
                  name: 'session_tool',
                  label: 'Session Tool',
                  description: 'Tool registered during session_start',
                  parameters: { type: 'object', properties: {} },
                  execute: async () => ({ content: [{ type: 'text', text: 'ok' }], details: {} })
                });
                ctx.ui.notify('registered session_tool', 'info');
              });
            }
            """);
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);
        await host.StartAsync(CancellationToken.None);

        await host.ForwardEventAsync(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("startup")), CancellationToken.None);

        Assert.Contains(registry.Tools, tool => tool.Value.Name == "session_tool");
    }

    [Fact]
    public async Task TypeScriptExtensionsCanQueryRuntimeToolListsDuringActivation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              const tools = await pi.getAllTools();
              pi.registerCommand(`tools-${tools.join('-')}`, { description: 'Observed tools', handler: () => tools.join(',') });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            GetAllToolsAsync = _ => Task.FromResult<IReadOnlyList<string>>(["read", "extension_search"])
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "tools-read-extension_search");
    }

    [Fact]
    public async Task TypeScriptExtensionsCanQueryRuntimeCommandsSynchronouslyDuringActivation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-commands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              const commands = pi.getCommands();
              if (!Array.isArray(commands)) throw new Error('getCommands did not return an array');
              const skill = commands.find(command => command.source === 'skill' && command.name === 'skill:research');
              const prompt = commands.find(command => command.source === 'prompt' && command.name === 'prompt:release');
              pi.registerCommand(`commands-${skill?.sourceInfo?.scope}-${prompt?.sourceInfo?.origin}`, {
                description: 'Observed commands',
                handler: () => commands.map(command => command.name).join(',')
              });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            GetCommandsAsync = _ => Task.FromResult<IReadOnlyList<ExtensionCommandInfo>>([
                new("skill:research", "Research skill", "skill", new ExtensionCommandSourceInfo("/repo/skills/research/SKILL.md", "skills", "project", "top-level", "/repo/skills")),
                new("prompt:release", "Release prompt", "prompt", new ExtensionCommandSourceInfo("/repo/prompts/release.md", "prompts", "project", "top-level", "/repo/prompts"))
            ])
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "commands-project-top-level");
    }

    [Fact]
    public async Task TypeScriptExtensionCanRegisterSkill()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.skills.register({
                name: 'ts-skill',
                description: 'Skill from TypeScript',
                content: 'Use TypeScript skill.',
                filePath: '/repo/skills/ts-skill/SKILL.md'
              });
            }
            """);

        var skill = Assert.Single(registry.Skills);
        Assert.Equal("ts-skill", skill.Value.Name);
        Assert.Equal("Skill from TypeScript", skill.Value.Description);
    }

    [Fact]
    public async Task TypeScriptSkillRegistrationSupportsTopLevelAlias()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerSkill({ name: 'alias-skill', description: 'Alias skill', content: 'body', filePath: '/repo/alias/SKILL.md' });
            }
            """);

        Assert.Contains(registry.Skills, skill => skill.Value.Name == "alias-skill");
    }

    [Fact]
    public async Task TypeScriptExtensionsCanReadAndSelectSkills()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              const skills = await pi.skills.list();
              await pi.skills.select(skills.map(skill => skill.name ?? skill.Name));
            }
            """);
        var selected = Array.Empty<string>();
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            GetAllSkillsAsync = _ => Task.FromResult<IReadOnlyList<ExtensionSkillRegistration>>([
                new("alpha", "Alpha", "body", "/repo/alpha/SKILL.md")
            ]),
            SetSelectedSkillsAsync = (names, _) => { selected = names.ToArray(); return Task.CompletedTask; }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);

        Assert.Equal(["alpha"], selected);
    }

    [Fact]
    public async Task TsPromptRegisterSectionRegistersPromptSection()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.prompt.registerSection({ id: "team-rules", slot: "instructions", priority: 50, content: "Prefer team conventions." });
            }
            """);

        var section = Assert.Single(registry.PromptSections);
        Assert.Equal("team-rules", section.Value.Id);
        Assert.Equal("instructions", section.Value.Placement.Slot);
        Assert.Equal(50, section.Value.Placement.Priority);
    }

    [Fact]
    public async Task TsPromptOverridePolicyIsHonored()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterPromptSection("native", new PromptSection("team-rules", PromptSectionKind.Extension, new MarkdownPromptContent("base"), new PromptPlacement("instructions")));
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.prompt.registerSection({ id: "team-rules", content: "override", override: "override" });
            }
            """);

        var section = Assert.Single(registry.PromptSections);
        Assert.Equal("override", ((MarkdownPromptContent)section.Value.Content).Markdown);
    }

    [Fact]
    public async Task TsPromptRegisterTransformRegistersDataOnlyTransform()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.prompt.registerTransform({ name: "append", appendMarkdown: "Appended rules." });
            }
            """);

        Assert.Single(registry.PromptTransforms);
    }

    [Fact]
    public async Task LoadAsyncReturnsBridgeTimingFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('timed', { description: 'Timed command', handler: () => 'ok' }); }");
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(extensionPath, result.ExtensionPath);
        Assert.NotNull(result.Timings);
        Assert.True(result.Timings!.Total >= 0);
        Assert.True(result.Timings.ModuleImport >= 0);
        Assert.True(result.Timings.Activation >= 0);
        Assert.True(result.Timings.RegistrationFlush >= 0);
    }

    [Fact]
    public async Task LoadAsyncReturnsStructuredResultWhenExtensionFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-failed-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate() { throw new Error('boom'); }");
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(extensionPath, result.ExtensionPath);
        Assert.Contains("boom", result.Error);
        Assert.NotNull(result.Timings);
        Assert.True(result.Timings!.Total >= 0);
    }

    [Fact]
    public async Task LoadManyAsyncLoadsMultipleExtensionsInOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var firstPath = Path.Combine(dir, "first.mjs");
        var secondPath = Path.Combine(dir, "second.mjs");
        await File.WriteAllTextAsync(firstPath, "export default function activate(pi) { pi.registerTool({ name: 'first_tool', label: 'First', description: 'First tool', parameters: { type: 'object', properties: {} }, execute: async () => ({ content: [{ type: 'text', text: 'first' }] }) }); }");
        await File.WriteAllTextAsync(secondPath, "export default async function activate(pi) { const tools = await pi.getAllTools(); pi.registerCommand(`saw-${tools.includes('first_tool')}`, { description: 'Observed tool order', handler: () => tools.join(',') }); }");
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            GetAllToolsAsync = _ => Task.FromResult<IReadOnlyList<string>>(registry.Tools.Select(tool => tool.Value.Name).ToArray())
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadManyAsync([firstPath, secondPath], binding, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Collection(result.Results!, first => Assert.True(first.Ok), second => Assert.True(second.Ok));
        Assert.Contains(registry.Commands, command => command.Value.Name == "saw-true");
    }

    [Fact]
    public async Task ExtensionConsoleOutputDoesNotCorruptBridgeProtocol()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-stdout-noise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
                console.log('extension wrote to stdout');
                pi.registerTool({
                    name: 'stdout_noise_tool',
                    label: 'Stdout Noise Tool',
                    description: 'Tool registered after console output',
                    parameters: { type: 'object', properties: {} },
                    execute: async () => ({ content: [{ type: 'text', text: 'ok' }] })
                });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(registry.Tools, tool => tool.Value.Name == "stdout_noise_tool");
    }

    [Fact]
    public async Task ConcurrentLoadManyAsyncRequestsAreSerialized()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-concurrent-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var firstPath = Path.Combine(dir, "first.mjs");
        var secondPath = Path.Combine(dir, "second.mjs");
        var logPath = Path.Combine(dir, "load-order.txt");
        var serializedLogPath = JsonSerializer.Serialize(logPath);
        await File.WriteAllTextAsync(firstPath, $$"""
            import fs from 'node:fs/promises';
            const logPath = {{serializedLogPath}};
            export default async function activate(pi) {
                await fs.appendFile(logPath, 'first:start\n');
                await new Promise(resolve => setTimeout(resolve, 150));
                await fs.appendFile(logPath, 'first:end\n');
                pi.registerCommand('first-loaded', { description: 'First loaded', handler: () => 'ok' });
            }
            """);
        await File.WriteAllTextAsync(secondPath, $$"""
            import fs from 'node:fs/promises';
            const logPath = {{serializedLogPath}};
            export default async function activate(pi) {
                await fs.appendFile(logPath, 'second:start\n');
                await new Promise(resolve => setTimeout(resolve, 150));
                await fs.appendFile(logPath, 'second:end\n');
                pi.registerCommand('second-loaded', { description: 'Second loaded', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var firstTask = host.LoadManyAsync([firstPath], binding, CancellationToken.None);
        var secondTask = host.LoadManyAsync([secondPath], binding, CancellationToken.None);
        var first = await firstTask;
        var second = await secondTask;

        Assert.True(first.Results![0].Ok);
        Assert.True(second.Results![0].Ok);
        var log = await File.ReadAllLinesAsync(logPath);
        Assert.Equal(["first:start", "first:end", "second:start", "second:end"], log);
    }

    [Fact]
    public async Task LoadManyAsyncKeepsPartialFailureAtOwningPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-batch-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var failPath = Path.Combine(dir, "fail.mjs");
        var okPath = Path.Combine(dir, "ok.mjs");
        await File.WriteAllTextAsync(failPath, "export default function activate() { throw new Error('batch boom'); }");
        await File.WriteAllTextAsync(okPath, "export default function activate(pi) { pi.registerCommand('after-failure', { description: 'Still loaded', handler: () => 'ok' }); }");
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadManyAsync([failPath, okPath], binding, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Collection(
            result.Results!,
            failed =>
            {
                Assert.False(failed.Ok);
                Assert.Equal(failPath, failed.ExtensionPath);
                Assert.Contains("batch boom", failed.Error);
            },
            succeeded =>
            {
                Assert.True(succeeded.Ok);
                Assert.Equal(okPath, succeeded.ExtensionPath);
            });
        Assert.Contains(registry.Commands, command => command.Value.Name == "after-failure");
    }

    [Fact]
    public async Task LoadManyAsyncReportsRegistrationFlushFailureForConflictingTool()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-batch-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var firstPath = Path.Combine(dir, "first.mjs");
        var secondPath = Path.Combine(dir, "second.mjs");
        var source = "export default function activate(pi) { pi.registerTool({ name: 'duplicate_tool', label: 'Duplicate', description: 'Duplicate tool', parameters: { type: 'object', properties: {} }, execute: async () => ({ content: [{ type: 'text', text: 'ok' }] }) }); }";
        await File.WriteAllTextAsync(firstPath, source);
        await File.WriteAllTextAsync(secondPath, source);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadManyAsync([firstPath, secondPath], binding, CancellationToken.None);

        var results = Assert.IsAssignableFrom<IReadOnlyList<TsExtensionLoadResult>>(result.Results);
        Assert.Equal(2, results.Count);
        var success = Assert.Single(results, loadResult => loadResult.Ok);
        var failure = Assert.Single(results, loadResult => !loadResult.Ok);
        var duplicatePaths = new[] { firstPath, secondPath };
        Assert.Contains(success.ExtensionPath, duplicatePaths);
        Assert.Contains(failure.ExtensionPath, duplicatePaths);
        Assert.NotEqual(success.ExtensionPath, failure.ExtensionPath);
        Assert.Contains("already registered", failure.Error);
        Assert.Single(registry.Tools, tool => tool.Value.Name == "duplicate_tool");
    }

    [Fact]
    public async Task TypeScriptTranspileCacheHitsAcrossBridgeProcesses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-cache-hit-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any) { pi.registerCommand('cached', { description: 'Cached command', handler: () => 'ok' }); }");
        var first = await LoadTypeScriptExtensionAsync(extensionPath, dir, cacheDir);

        var second = await LoadTypeScriptExtensionAsync(extensionPath, dir, cacheDir);

        Assert.True(first.Ok);
        Assert.True(first.Timings!.CacheMisses > 0);
        Assert.True(second.Ok);
        Assert.True(second.Timings!.CacheHits > 0);
        Assert.Empty(Directory.EnumerateFiles(dir, "*.pisharp.mjs", SearchOption.TopDirectoryOnly));
        Assert.NotEmpty(Directory.EnumerateFiles(cacheDir, "*.mjs", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TypeScriptTranspileCacheInvalidatesWhenSourceChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-cache-source-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any) { pi.registerCommand('cached-one', { description: 'Cached command', handler: () => 'one' }); }");
        var first = await LoadTypeScriptExtensionAsync(extensionPath, dir, cacheDir);
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any) { pi.registerCommand('cached-two', { description: 'Cached command', handler: () => 'two' }); }");

        var second = await LoadTypeScriptExtensionAsync(extensionPath, dir, cacheDir);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.True(second.Timings!.CacheMisses > 0);
    }

    [Fact]
    public async Task TypeScriptTranspileCacheInvalidatesWhenDependencyChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-cache-dep-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        var dependencyPath = Path.Combine(dir, "dep.ts");
        await File.WriteAllTextAsync(extensionPath, "import { name } from './dep.js'; export default function activate(pi: any) { pi.registerCommand(name, { description: 'Cached command', handler: () => name }); }");
        await File.WriteAllTextAsync(dependencyPath, "export const name: string = 'dep-one';");
        var first = await LoadTypeScriptExtensionAsync(extensionPath, dir, cacheDir);
        await File.WriteAllTextAsync(dependencyPath, "export const name: string = 'dep-two';");

        var second = await LoadTypeScriptExtensionAsync(extensionPath, dir, cacheDir);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.True(second.Timings!.CacheMisses > 0);
    }

    [Fact]
    public async Task TypeScriptTranspileCacheFallsBackWhenCacheDirectoryIsUnavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-cache-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cacheFile = Path.Combine(dir, "cache-file");
        await File.WriteAllTextAsync(cacheFile, "not a directory");
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any) { pi.registerCommand('fallback', { description: 'Fallback command', handler: () => 'ok' }); }");

        var result = await LoadTypeScriptExtensionAsync(extensionPath, dir, cacheFile);

        Assert.True(result.Ok);
        Assert.NotNull(result.Timings);
        Assert.True(result.Timings!.CacheFallbacks > 0);
        Assert.Empty(Directory.EnumerateFiles(dir, "*.pisharp.mjs", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task DescriptorCacheReplayRegistersFlagsPromptsAndProxyTool()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-descriptor-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi: any) {
              pi.registerFlag('cached-flag', { description: 'Cached flag', type: 'string', defaultValue: 'yes' });
              pi.prompt.registerSection({ id: 'cached-section', content: 'Cached prompt.' });
              pi.registerTool({
                name: 'cached_tool',
                label: 'Cached Tool',
                description: 'Tool from descriptor cache',
                parameters: { type: 'object', properties: {} },
                execute: async () => ({ content: [{ type: 'text', text: 'real handler' }] })
              });
            }
            """);
        var firstRegistry = new ExtensionRegistry();
        var firstBinding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), firstRegistry, firstBinding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, firstBinding, CancellationToken.None);
            Assert.True(loaded.Ok);
            Assert.NotNull(loaded.Descriptor);
        }

        var secondRegistry = new ExtensionRegistry();
        var secondBinding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), secondRegistry, secondBinding);
        await secondHost.StartAsync(CancellationToken.None);

        var replayed = await secondHost.ReplayCachedDescriptorAsync(extensionPath, secondBinding, CancellationToken.None);

        Assert.True(replayed);
        Assert.Contains(secondRegistry.Flags, flag => flag.Value.Name == "cached-flag");
        Assert.Contains(secondRegistry.PromptSections, section => section.Value.Id == "cached-section");
        var tool = Assert.Single(secondRegistry.Tools, tool => tool.Value.Name == "cached_tool").Value;
        using var args = JsonDocument.Parse("{}");
        var result = await tool.ExecuteAsync("tool-call", args.RootElement.Clone(), CancellationToken.None);
        Assert.Equal("real handler", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task DescriptorCacheReplayRegistersSkillMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-skill-descriptor-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi: any) {
              pi.skills.register({ name: 'cached-skill', description: 'Cached skill', content: 'body', filePath: '/repo/cached/SKILL.md' });
            }
            """);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok);
            Assert.NotNull(loaded.Descriptor);
        }

        var secondRegistry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), secondRegistry, binding);
        await secondHost.StartAsync(CancellationToken.None);

        var replayed = await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(replayed);
        Assert.Contains(secondRegistry.Skills, skill => skill.Value.Name == "cached-skill");
    }

    [Fact]
    public async Task DescriptorCacheReplayRejectsStaleSource()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-descriptor-stale-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any) { pi.registerCommand('one', { description: 'one', handler: () => 'one' }); }");
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok);
        }
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi: any) { pi.registerCommand('two', { description: 'two', handler: () => 'two' }); }");
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding);
        await secondHost.StartAsync(CancellationToken.None);

        var replayed = await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None);

        Assert.False(replayed);
    }

    [Fact]
    public async Task DescriptorCacheReplayRejectsStaleDependency()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-descriptor-stale-dep-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        var dependencyPath = Path.Combine(dir, "dep.ts");
        await File.WriteAllTextAsync(extensionPath, "import { name } from './dep.js'; export default function activate(pi: any) { pi.registerCommand(name, { description: name, handler: () => name }); }");
        await File.WriteAllTextAsync(dependencyPath, "export const name: string = 'dep-one';");
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok);
        }
        await File.WriteAllTextAsync(dependencyPath, "export const name: string = 'dep-two';");
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding);
        await secondHost.StartAsync(CancellationToken.None);

        var replayed = await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None);

        Assert.False(replayed);
    }

    [Fact]
    public async Task DescriptorCacheReplayUsesInstalledPackageVersionForDependencyFreshness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-descriptor-package-version-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        var packageRoot = Path.Combine(dir, "node_modules", "cached-package");
        Directory.CreateDirectory(packageRoot);
        await InstallFakeTypeScriptCompilerAsync(dir);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "package.json"), "{ \"name\": \"cached-package\", \"version\": \"1.0.0\" }");
        var extensionPath = Path.Combine(packageRoot, "index.ts");
        var dependencyPath = Path.Combine(packageRoot, "dep.ts");
        await File.WriteAllTextAsync(extensionPath, "import { name } from './dep.js'; export default function activate(pi: any) { pi.registerCommand(name, { description: name, handler: () => name }); }");
        await File.WriteAllTextAsync(dependencyPath, "export const name: string = 'dep-one';");
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok);
        }
        await File.WriteAllTextAsync(dependencyPath, "export const name: string = 'dep-two';");

        await using (var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await secondHost.StartAsync(CancellationToken.None);
            var replayed = await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None);

            Assert.True(replayed);
        }

        await File.WriteAllTextAsync(Path.Combine(packageRoot, "package.json"), "{ \"name\": \"cached-package\", \"version\": \"1.0.1\" }");
        await using var thirdHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding);
        await thirdHost.StartAsync(CancellationToken.None);

        var staleReplay = await thirdHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None);

        Assert.False(staleReplay);
    }

    [Fact]
    public async Task DescriptorCacheReplayRegistersProviderMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-descriptor-provider-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi: any) {
              pi.registerProvider({
                name: 'cached-provider',
                api: 'cached-provider-api',
                hasCustomStreamHandler: true,
                models: [{ provider: 'cached-provider', id: 'cached-model', name: 'Cached Model' }]
              });
            }
            """);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok);
        }

        var secondRegistry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), secondRegistry, binding);
        await secondHost.StartAsync(CancellationToken.None);

        var replayed = await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(replayed);
        Assert.Contains(secondRegistry.Providers, provider => provider.Value.Api == "cached-provider-api");
    }

    [Fact]
    public async Task TypeScriptBeforeAgentStartCanReturnSystemPromptMutation()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.on('before_agent_start', event => ({ systemPrompt: event.systemPrompt.replace('ORIGINAL', 'MUTATED') }));
            }
            """);
        var before = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.BeforeAgentStart("hello", null, "ORIGINAL PROMPT", Array.Empty<IAgentTool>()));
        var extensionEvent = ExtensionEventMapper.Map(before);

        await host.ForwardExtensionEventAsync(extensionEvent, CancellationToken.None);

        Assert.Equal("MUTATED PROMPT", extensionEvent.ModifiedSystemPrompt);
    }

    [Fact]
    public async Task TypeScriptBeforePromptRenderCanReturnDocumentPatch()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.on('before_prompt_render', event => {
                if (!event.sections.some(section => section.id === 'skills.available')) return undefined;
                return { patch: { replaceSections: [{ id: 'skills.available', slot: 'skills', kind: 'skills', contentType: 'raw', content: 'PATCHED SKILLS' }] } };
              });
            }
            """);
        var document = new SystemPromptDocument([
            new PromptSection("skills.available", PromptSectionKind.Skills, new RawPromptContent("ORIGINAL SKILLS"), new PromptPlacement("skills"))
        ], []);
        var applier = new PromptDocumentPatchApplier();
        var payload = new PromptDocumentHookPayload("hello", applier.ToSectionDtos(document), document.Diagnostics, Array.Empty<IAgentTool>());
        var before = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.BeforePromptRender(
            "hello",
            null,
            PromptContext(),
            document,
            Array.Empty<IAgentTool>()));
        var extensionEvent = new ExtensionEvent(ExtensionEventNames.BeforePromptRender, before, payload);

        await host.ForwardExtensionEventAsync(extensionEvent, CancellationToken.None);

        Assert.NotNull(extensionEvent.ModifiedPromptDocument);
        var modified = extensionEvent.ModifiedPromptDocument!;
        var section = Assert.Single(modified.Sections, section => section.Id == "skills.available");
        Assert.Equal("PATCHED SKILLS", Assert.IsType<RawPromptContent>(section.Content).Text);
    }

    [Fact]
    public async Task TypeScriptExtensionsCanProvideAndConsumeServicesInLoadOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-services-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var providerPath = Path.Combine(dir, "provider.mjs");
        var consumerPath = Path.Combine(dir, "consumer.mjs");
        await File.WriteAllTextAsync(providerPath, """
            export default function activate(pi) {
              pi.extensions.provide('test.math', { double: (value) => value * 2 });
            }
            """);
        await File.WriteAllTextAsync(consumerPath, """
            export default async function activate(pi) {
              const math = await pi.extensions.waitFor('test.math', { timeoutMs: 1000 });
              pi.registerCommand(`service-${math.double(21)}`, { description: 'Observed service', handler: () => 'ok' });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadManyAsync([providerPath, consumerPath], binding, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.All(result.Results!, item => Assert.True(item.Ok, item.Error));
        Assert.Contains("test.math", result.Results![0].Descriptor!.ProvidesServices!);
        Assert.Contains("test.math", result.Results![1].Descriptor!.ConsumesServices!);
        Assert.Equal("eager", result.Results![0].Descriptor!.Activation);
        Assert.Equal("eager", result.Results![1].Descriptor!.Activation);
        Assert.Contains(registry.Commands, command => command.Value.Name == "service-42");
    }

    [Fact]
    public async Task DescriptorCacheReplaySkipsServiceExtensionsSoTheyActivateEagerly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-service-descriptor-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi: any) {
              pi.extensions.provide('test.cached-service', { value: 1 });
              pi.registerCommand('service-provider-loaded', { description: 'Loaded', handler: () => 'ok' });
            }
            """);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(extensionPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok, loaded.Error);
            Assert.Contains("test.cached-service", loaded.Descriptor!.ProvidesServices!);
        }

        var secondRegistry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), secondRegistry, binding);
        await secondHost.StartAsync(CancellationToken.None);

        var replayed = await secondHost.ReplayCachedDescriptorAsync(extensionPath, binding, CancellationToken.None);

        Assert.False(replayed);
        var loadedAgain = await secondHost.LoadAsync(extensionPath, binding, CancellationToken.None);
        Assert.True(loadedAgain.Ok, loadedAgain.Error);
        Assert.Contains(secondRegistry.Commands, command => command.Value.Name == "service-provider-loaded");
    }

    [Fact]
    public async Task CachedServiceProviderActivatesBeforeWaitingConsumer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-service-e2e-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var providerPath = Path.Combine(dir, "provider.ts");
        var consumerPath = Path.Combine(dir, "consumer.ts");
        await File.WriteAllTextAsync(providerPath, """
            export default function activate(pi: any) {
              pi.extensions.provide('test.cached-e2e', { value: 42 });
            }
            """);
        await File.WriteAllTextAsync(consumerPath, """
            export default async function activate(pi: any) {
              const service = await pi.extensions.waitFor('test.cached-e2e', { timeoutMs: 1000 });
              pi.registerCommand(`cached-service-${service.value}`, { description: 'Observed cached service', handler: () => 'ok' });
            }
            """);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using (var firstHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), new ExtensionRegistry(), binding))
        {
            await firstHost.StartAsync(CancellationToken.None);
            var loaded = await firstHost.LoadAsync(providerPath, binding, CancellationToken.None);
            Assert.True(loaded.Ok, loaded.Error);
            Assert.Contains("test.cached-e2e", loaded.Descriptor!.ProvidesServices!);
        }

        var registry = new ExtensionRegistry();
        await using var secondHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry, binding);
        await secondHost.StartAsync(CancellationToken.None);
        var replayed = await secondHost.ReplayCachedDescriptorAsync(providerPath, binding, CancellationToken.None);

        Assert.False(replayed);
        var result = await secondHost.LoadManyAsync([providerPath, consumerPath], binding, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.All(result.Results!, item => Assert.True(item.Ok, item.Error));
        Assert.Contains(registry.Commands, command => command.Value.Name == "cached-service-42");
    }

    [Fact]
    public async Task TypeScriptInputHandlersCanTransformAndHandleInput()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.on('input', event => event.text === '/hello' ? { action: 'transform', text: 'say hello' } : undefined);
              pi.on('input', event => event.text === '/handled' ? { action: 'handled' } : undefined);
            }
            """);

        var transform = new ExtensionEvent(ExtensionEventNames.Input, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("/hello", null, "interactive")), new ExtensionInputEvent("/hello", null, "interactive"));
        await host.ForwardExtensionEventAsync(transform, CancellationToken.None);
        Assert.Equal("transform", transform.InputResult!.Action);
        Assert.Equal("say hello", transform.InputResult.Text);

        var handled = new ExtensionEvent(ExtensionEventNames.Input, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("/handled", null, "interactive")), new ExtensionInputEvent("/handled", null, "interactive"));
        await host.ForwardExtensionEventAsync(handled, CancellationToken.None);
        Assert.Equal("handled", handled.InputResult!.Action);
    }

    [Fact]
    public async Task TypeScriptInputDispatchCompletesWhenCallerSynchronizationContextDoesNotPumpContinuations()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.on('input', event => ({ action: 'transform', text: `${event.text} transformed` }));
            }
            """);
        var evt = new ExtensionEvent(ExtensionEventNames.Input, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("start", null, "interactive")), new ExtensionInputEvent("start", null, "interactive"));

        var previousContext = SynchronizationContext.Current;
        Task dispatchTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            dispatchTask = host.ForwardExtensionEventAsync(evt, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("start transformed", evt.InputResult!.Text);
    }

    [Fact]
    public async Task TypeScriptInputHandlersChainTransformsInRegistrationOrder()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.on('input', event => ({ action: 'transform', text: `${event.text} one` }));
              pi.on('input', event => ({ action: 'transform', text: `${event.text} two` }));
            }
            """);
        var evt = new ExtensionEvent(ExtensionEventNames.Input, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("start", null, "interactive")), new ExtensionInputEvent("start", null, "interactive"));

        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);

        Assert.Equal("start one two", evt.InputResult!.Text);
    }

    [Fact]
    public async Task TypeScriptSessionBeforeHooksCanCancel()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.on('session_before_switch', event => event.targetSessionFile?.includes('blocked') ? { cancel: true, reason: 'blocked switch' } : undefined);
              pi.on('session_before_fork', event => event.entryId === 'blocked-entry' ? { cancel: true, reason: 'blocked fork' } : undefined);
            }
            """);
        var beforeSwitch = new ExtensionEvent(ExtensionEventNames.SessionBeforeSwitch, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeSwitch("resume", "blocked.jsonl", new { }, new { }, CancellationToken.None)), new ExtensionSessionBeforeSwitchEvent("resume", "blocked.jsonl"));
        await host.ForwardExtensionEventAsync(beforeSwitch, CancellationToken.None);
        Assert.True(beforeSwitch.SessionChangeResult!.Cancel);
        Assert.Equal("blocked switch", beforeSwitch.SessionChangeResult.Reason);

        var beforeFork = new ExtensionEvent(ExtensionEventNames.SessionBeforeFork, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeFork("blocked-entry", "at", new { }, new { }, CancellationToken.None)), new ExtensionSessionBeforeForkEvent("blocked-entry", "at"));
        await host.ForwardExtensionEventAsync(beforeFork, CancellationToken.None);
        Assert.True(beforeFork.SessionChangeResult!.Cancel);
        Assert.Equal("blocked fork", beforeFork.SessionChangeResult.Reason);
    }

    [Fact]
    public async Task TypeScriptRegisterMessageRendererUsesJavaScriptPiCallbackShape()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerMessageRenderer("approval-card", (message, options, theme) => ({
                render(width) {
                  return [
                    `card:${message.customType}`,
                    `content:${message.content}`,
                    `details:${message.details.requestId}`,
                    `expanded:${options.expanded}`,
                    `theme:${theme ? "yes" : "no"}`,
                    `width:${width}`,
                  ];
                }
              }));
            }
            """);

        var renderer = Assert.Single(registry.Renderers);
        Assert.Equal("approval-card", renderer.Value.Name);
        Assert.Equal("approval-card", renderer.Value.CustomType);
        Assert.NotNull(renderer.Value.Handler);
        var context = new ExtensionChatRowRenderContext(
            ExtensionChatRowType.Custom, "custom", "Approve?",
            Width: 72,
            CustomType: "approval-card",
            CustomContent: "Approve?",
            CustomDetails: JsonDocument.Parse("{\"requestId\":\"r-1\"}").RootElement.Clone(),
            CustomDisplay: true);
        var rows = renderer.Value.Handler(context);

        Assert.Contains(rows, row => row.Text == "card:approval-card");
        Assert.Contains(rows, row => row.Text == "content:Approve?");
        Assert.Contains(rows, row => row.Text == "details:r-1");
        Assert.Contains(rows, row => row.Text == "expanded:false");
        Assert.Contains(rows, row => row.Text == "theme:yes");
        Assert.Contains(rows, row => row.Text == "width:72");
    }

    [Fact]
    public async Task TypeScriptRegisterMessageDecoratorRegistersProxy()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerMessageDecorator("my-decorator", { order: 10 }, (context, rows) => {
                return rows.map(row => ({ ...row, text: `${context.data.title}:★ ${row.text}` }));
              });
            }
            """);

        Assert.Contains(registry.Decorators, d => d.Value.Name == "my-decorator");
        var decorator = Assert.Single(registry.Decorators, d => d.Value.Name == "my-decorator");
        Assert.Equal("my-decorator", decorator.Value.CustomType);
        Assert.NotNull(decorator.Value.Handler);
        var context = new ExtensionChatRowRenderContext(
            ExtensionChatRowType.Custom, "custom", "original",
            Metadata: new Dictionary<string, string> { ["customType"] = "my-decorator", ["title"] = "decorated" },
            CustomType: "my-decorator");
        var rows = decorator.Value.Handler(context, [new ExtensionChatRow("line", ExtensionChatRowKind.Custom)]);
        var row = Assert.Single(rows);
        Assert.Equal("decorated:★ line", row.Text);
    }

    [Fact]
    public async Task TypeScriptRegisterMessageRendererCanOverrideBuiltInRows()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerMessageRenderer({
                name: "assistant-renderer",
                rowType: "Assistant",
                override: "overrideBuiltIn",
                handler: () => ({ lines: ["assistant-rendered"] })
              });
            }
            """);

        var renderer = Assert.Single(registry.Renderers, r => r.Value.Name == "assistant-renderer");
        Assert.Equal(ExtensionChatRowType.Assistant, renderer.Value.RowType);
        Assert.Equal(ExtensionOverridePolicy.OverrideBuiltIn, renderer.Value.Override);
    }

    [Fact]
    public async Task TypeScriptMessageRendererDisposeRestoresPreviousCustomTypeRenderer()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerMessageRenderer({
                name: "first-renderer",
                customType: "custom-card",
                handler: () => ({ render() { return ["first-rendered"]; } })
              });
              const second = pi.registerMessageRenderer({
                name: "second-renderer",
                customType: "custom-card",
                handler: () => ({ render() { return ["second-rendered"]; } })
              });
              second.dispose();
            }
            """);

        var renderer = Assert.Single(registry.Renderers);
        Assert.Equal("first-renderer", renderer.Value.Name);
        var rows = renderer.Value.Handler!(new ExtensionChatRowRenderContext(
            ExtensionChatRowType.Custom, "custom", "text",
            Metadata: new Dictionary<string, string> { ["customType"] = "custom-card" },
            CustomType: "custom-card"));
        var row = Assert.Single(rows);
        Assert.Equal("first-rendered", row.Text);
    }

    [Fact]
    public async Task TypeScriptMessageDecoratorDisposeRemovesRegistryRegistration()
    {
        var registry = new ExtensionRegistry();
        await using var host = await CreateHostWithExtensionAsync(registry, """
            export default function activate(pi) {
              pi.registerMessageDecorator({
                name: "first-decorator",
                customType: "custom-card",
                handler: (_context, rows) => rows
              });
              const second = pi.registerMessageDecorator({
                name: "second-decorator",
                customType: "custom-card",
                handler: (_context, rows) => rows
              });
              second.unsubscribe();
            }
            """);

        var decorator = Assert.Single(registry.Decorators);
        Assert.Equal("first-decorator", decorator.Value.Name);
    }

    [Fact]
    public void ContractsIncludeCommandUiProviderMessages()
    {
        Assert.Equal("ext", new TsExtensionRegistration("ext", "extension:ts:ext", "command", "cmd", new { }).ExtensionId);
        Assert.Equal("req", new TsUiRequest("req", "ext", "notify", "Title", "Message", null, null).RequestId);
        Assert.Equal("api", new TsProviderCallbackRequest("api", "complete", new { }).ProviderApi);
    }

    [Fact]
    public void JsonElementPayloadValueExtractionDoesNotSerializeWholePayload()
    {
        var method = typeof(TsExtensionHost).GetMethod("GetPayloadValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var getPayloadValue = method.CreateDelegate<Func<object?, string, JsonElement>>();
        using var document = JsonDocument.Parse($$"""
            {
              "name": "flag-name",
              "padding": "{{new string('x', 100_000)}}"
            }
            """);

        _ = getPayloadValue(document.RootElement, "name");
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
        {
            var value = getPayloadValue(document.RootElement, "name");
            Assert.Equal("flag-name", value.GetString());
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000, $"Expected JsonElement payload extraction to avoid serializing the whole payload. Allocated {allocated:N0} bytes.");
    }

    /// <summary>
    /// Verifies that pi.resources.list() returns resource metadata (kind, path) and
    /// pi.resources.read(path) returns the loaded content for a skill or prompt resource.
    ///
    /// Bridge contract for list_resources:
    ///   Returns a JSON array of items with at minimum kind and path fields
    ///   representing all loaded resources (SkillPaths, PromptTemplatePaths, etc.).
    ///
    /// Bridge contract for read_resource:
    ///   Accepts { uri: string } where uri must be a path from list_resources.
    ///   Returns { path, content } with the loaded resource content.
    ///   Unknown/arbitrary paths must return an error, not fall back to filesystem read.
    ///
    /// Current RED state: TsExtensionHost.RuntimeActionAsync does not handle
    /// list_resources or read_resource, so the extension activation fails.
    /// </summary>
    [Fact]
    public async Task TypeScriptExtensionResourcesListReadReturnsMetadataAndContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        var promptPath = Path.Combine(dir, "test-prompt.md");
        await File.WriteAllTextAsync(promptPath, "This is a test prompt resource.");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              const resources = await pi.resources.list();
              const first = resources.find((r) => r.kind === "prompt" || r.kind === "skill");
              if (!first) throw new Error("No prompt or skill resource found");
              const read = await pi.resources.read(first.path);
              pi.registerCommand('resources-list-read', {
                description: JSON.stringify({ first: { kind: first.kind, path: first.path }, read: { path: read.path, contentLength: read.content?.length } }),
                handler: () => 'ok'
              });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            ResourceItems = [new ExtensionResourceItem("prompt", promptPath)],
            ReadResourceAsync = async (path, _) => new ExtensionResourceContent(path, await File.ReadAllTextAsync(path))
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var cmd = Assert.Single(registry.Commands, c => c.Value.Name == "resources-list-read");
        using var meta = JsonDocument.Parse(cmd.Value.Description);
        var firstProp = meta.RootElement.GetProperty("first");
        Assert.NotNull(firstProp.GetProperty("kind").GetString());
        Assert.NotNull(firstProp.GetProperty("path").GetString());
        Assert.Equal(promptPath, firstProp.GetProperty("path").GetString());
        var readProp = meta.RootElement.GetProperty("read");
        Assert.NotNull(readProp.GetProperty("path").GetString());
        Assert.True(readProp.GetProperty("contentLength").GetInt32() > 0);
    }

    [Fact]
    public async Task TypeScriptUserBashHandlerReturnsResult()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-user-bash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on("user_bash", (event) => {
                if (event.command === "status") {
                  return { result: { command: event.command, exitCode: 0, output: "ok", error: "" } };
                }
              });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var evt = new ExtensionEvent(ExtensionEventNames.UserBash, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), new ExtensionUserBashPayload("status", false, dir));
        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);

        Assert.NotNull(evt.UserBashResult);
        Assert.NotNull(evt.UserBashResult.Result);
        Assert.Equal(0, evt.UserBashResult.Result.ExitCode);
        Assert.Equal("ok", evt.UserBashResult.Result.Output);
    }

    [Fact]
    public async Task TypeScriptUserBashHandlerFirstResultWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-user-bash-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on("user_bash", () => ({ result: { command: "first", exitCode: 0, output: "first", error: "" } }));
              pi.on("user_bash", () => ({ result: { command: "second", exitCode: 1, output: "second", error: "" } }));
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var evt = new ExtensionEvent(ExtensionEventNames.UserBash, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), new ExtensionUserBashPayload("test", false, dir));
        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);

        Assert.NotNull(evt.UserBashResult);
        Assert.NotNull(evt.UserBashResult.Result);
        Assert.Equal("first", evt.UserBashResult.Result.Output);
    }

    [Fact]
    public async Task TypeScriptUserBashSkipWhenNoMatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-user-bash-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on("user_bash", (event) => {
                if (event.command === "match") {
                  return { result: { command: event.command, exitCode: 0, output: "matched", error: "" } };
                }
              });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var evt = new ExtensionEvent(ExtensionEventNames.UserBash, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), new ExtensionUserBashPayload("no-match", false, dir));
        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);

        Assert.Null(evt.UserBashResult);
    }

    [Fact]
    public async Task TypeScriptUserBashHandlerFailureIsolated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-user-bash-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on("user_bash", () => { throw new Error("first handler failed"); });
              pi.on("user_bash", () => ({ result: { command: "status", exitCode: 0, output: "ok", error: "" } }));
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var evt = new ExtensionEvent(ExtensionEventNames.UserBash, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), new ExtensionUserBashPayload("status", false, dir));
        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);

        Assert.NotNull(evt.UserBashResult);
        Assert.NotNull(evt.UserBashResult.Result);
        Assert.Equal("ok", evt.UserBashResult.Result.Output);
    }

    [Fact]
    public async Task TypeScriptResourcesDiscoverContributesPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-disc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on("resources_discover", () => ({
                skillPaths: ["./skills"],
                promptPaths: ["./prompts"],
                themePaths: ["./themes"]
              }));
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var evt = new ExtensionEvent(ExtensionEventNames.ResourcesDiscover, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), new ExtensionResourcesDiscoverPayload(dir, "startup"));
        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);

        Assert.NotNull(evt.ResourcesDiscoverResult);
        Assert.Contains(evt.ResourcesDiscoverResult.SkillPaths, path => path.EndsWith("skills", StringComparison.Ordinal));
        Assert.Contains(evt.ResourcesDiscoverResult.PromptPaths, path => path.EndsWith("prompts", StringComparison.Ordinal));
        Assert.Contains(evt.ResourcesDiscoverResult.ThemePaths, path => path.EndsWith("themes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeScriptResourcesDiscoverHandlerFailureIsolated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-disc-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on("resources_discover", () => {
                throw new Error("first handler failed");
              });
              pi.on("resources_discover", () => ({
                skillPaths: ["./skills"]
              }));
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var evt = new ExtensionEvent(ExtensionEventNames.ResourcesDiscover, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), new ExtensionResourcesDiscoverPayload(dir, "startup"));
        await host.ForwardExtensionEventAsync(evt, CancellationToken.None);

        Assert.NotNull(evt.ResourcesDiscoverResult);
        Assert.Contains(evt.ResourcesDiscoverResult.SkillPaths, path => path.EndsWith("skills", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeScriptExtensionResourcesListReadRejectsArbitraryPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-resources-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        var secretPath = Path.Combine(dir, "secret.txt");
        var jsSecretPath = secretPath.Replace("\\", "\\\\");
        await File.WriteAllTextAsync(secretPath, "sensitive data");
        var knownPath = Path.Combine(dir, "known-prompt.md");
        await File.WriteAllTextAsync(knownPath, "known content");
        await File.WriteAllTextAsync(extensionPath, $$"""
            export default async function activate(pi) {
              try {
                const result = await pi.resources.read('{{jsSecretPath}}');
                if (result && result.error) {
                  pi.registerCommand('resources-read-unknown-path', {
                    description: JSON.stringify({ rejected: true, error: result.error }),
                    handler: () => 'ok'
                  });
                } else if (result && result.content) {
                  pi.registerCommand('resources-read-unknown-path', {
                    description: JSON.stringify({ rejected: false, contentLength: result.content.length }),
                    handler: () => 'ok'
                  });
                }
              } catch (e) {
                pi.registerCommand('resources-read-unknown-path', {
                  description: JSON.stringify({ rejected: true, error: e.message }),
                  handler: () => 'ok'
                });
              }
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            ResourceItems = [new ExtensionResourceItem("prompt", knownPath)],
            ReadResourceAsync = async (path, _) => new ExtensionResourceContent(path, await File.ReadAllTextAsync(path))
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var cmd = Assert.Single(registry.Commands, c => c.Value.Name == "resources-read-unknown-path");
        using var meta = JsonDocument.Parse(cmd.Value.Description);
        Assert.True(meta.RootElement.TryGetProperty("rejected", out var rejected) && rejected.GetBoolean(),
            "Expected arbitrary path read to be rejected, but it was not.");
    }

    [Fact]
    public async Task TypeScriptCrossExtensionEventBusDispatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-eventbus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var listenerPath = Path.Combine(dir, "listener.mjs");
        var emitterPath = Path.Combine(dir, "emitter.mjs");
        await File.WriteAllTextAsync(listenerPath, """
            export default function activate(pi) {
              pi.events.on("my:notification", async (payload) => {
                await pi.session.setName(payload.name);
              });
            }
            """);
        await File.WriteAllTextAsync(emitterPath, """
            export default async function activate(pi) {
              await pi.events.emit("my:notification", { name: "from-event" });
            }
            """);
        string? capturedName = null;
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            SetSessionNameAsync = (name, _) => { capturedName = name; return Task.CompletedTask; }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadManyAsync([listenerPath, emitterPath], binding, CancellationToken.None);

        Assert.True(result.Ok, result.Results?.FirstOrDefault(r => !r.Ok)?.Error);
        Assert.Equal("from-event", capturedName);
    }

    [Fact]
    public async Task TypeScriptEmitIsolatesThrowingNativeHandlerAndCapturesException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-native-throw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              await pi.events.emit("my:native", { value: "hello" });
            }
            """);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("test-source", "my:native", (_, _) => throw new InvalidOperationException("native handler failed"));
        string? capturedSideEffect = null;
        registry.RegisterHandler("test-source-2", "my:native", (_, _) =>
        {
            capturedSideEffect = "second ran";
            return Task.CompletedTask;
        });
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var result = await host.LoadAsync(extensionPath, binding, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("second ran", capturedSideEffect);
        Assert.Contains(host.EmitDiagnostics, d => d.Message.Contains("native handler failed"));
    }

    [Fact]
    public async Task NativeEmitEventReachesTypeScriptHandler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-native-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.events.on("my:event", async (payload) => {
                await pi.session.setName(payload.name);
              });
              pi.registerCommand("ping", { description: "ping", handler: () => "pong" });
            }
            """);
        string? capturedName = null;
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            SetSessionNameAsync = (name, _) => { capturedName = name; return Task.CompletedTask; }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);
        await host.LoadAsync(extensionPath, binding, CancellationToken.None);

        var bus = new ExtensionEventBus(registry, "test-source", binding.EmitEventAsync);
        await bus.EmitAsync("my:event", new { name = "from-native" }, CancellationToken.None);

        Assert.Equal("from-native", capturedName);
    }

    [Fact]
    public async Task NativeEmitEventAwaitsTypeScriptHandlersAndIsolatesFailures()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-native-emit-await-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var throwingPath = Path.Combine(dir, "throwing.mjs");
        var listenerPath = Path.Combine(dir, "listener.mjs");
        await File.WriteAllTextAsync(throwingPath, """
            export default function activate(pi) {
              pi.events.on("my:event", async () => {
                throw new Error("ts handler failed");
              });
            }
            """);
        await File.WriteAllTextAsync(listenerPath, """
            export default function activate(pi) {
              pi.events.on("my:event", async (payload) => {
                await Promise.resolve();
                await pi.session.setName(payload.name);
              });
            }
            """);
        string? capturedName = null;
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            SetSessionNameAsync = (name, _) => { capturedName = name; return Task.CompletedTask; }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);
        var loadResult = await host.LoadManyAsync([throwingPath, listenerPath], binding, CancellationToken.None);
        Assert.True(loadResult.Ok, loadResult.Results?.FirstOrDefault(result => !result.Ok)?.Error);

        var bus = new ExtensionEventBus(registry, "test-source", binding.EmitEventAsync);
        await bus.EmitAsync("my:event", new { name = "from-native" }, CancellationToken.None);

        Assert.Equal("from-native", capturedName);
        Assert.Empty(bus.Diagnostics);
    }

    private static async Task<TsExtensionLoadResult> LoadTypeScriptExtensionAsync(string extensionPath, string workingDirectory, string cacheDirectory)
    {
        await InstallFakeTypeScriptCompilerAsync(workingDirectory);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(workingDirectory, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: workingDirectory, CacheDirectory: cacheDirectory), registry, binding);
        await host.StartAsync(CancellationToken.None);
        return await host.LoadAsync(extensionPath, binding, CancellationToken.None);
    }

    private static string ShimFileUrlPattern(string source)
    {
        const string marker = "pisharp-pi-coding-agent-shim.";
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "Expected cached module to import the Pi coding-agent compatibility shim.");
        var start = source.LastIndexOfAny(new[] { '\"', '\'' }, markerIndex);
        Assert.True(start >= 0, "Expected shim import specifier to be quoted.");
        var end = source.IndexOf(source[start], markerIndex);
        Assert.True(end > start, "Expected shim import specifier to be quoted.");
        return source[start..(end + 1)];
    }

    private static IDisposable TemporaryUserHome(string home)
    {
        var oldUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("USERPROFILE", home);
        Environment.SetEnvironmentVariable("HOME", home);
        return new DisposableAction(() =>
        {
            Environment.SetEnvironmentVariable("USERPROFILE", oldUserProfile);
            Environment.SetEnvironmentVariable("HOME", oldHome);
        });
    }

    private static async Task InstallFakeTypeScriptCompilerAsync(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "node_modules", "typescript", "lib"));
        await File.WriteAllTextAsync(Path.Combine(directory, "node_modules", "typescript", "package.json"), "{ \"name\": \"typescript\", \"main\": \"lib/typescript.js\" }");
        await File.WriteAllTextAsync(Path.Combine(directory, "node_modules", "typescript", "lib", "typescript.js"), """
            function stripParams(parameters) {
              return parameters.replace(/([A-Za-z_$][\w$]*)\s*:\s*[A-Za-z_$][\w$]*/g, '$1');
            }

            module.exports = {
              version: 'fake-local',
              ModuleKind: { ES2022: 99 },
              ScriptTarget: { ES2022: 99 },
              ModuleResolutionKind: { NodeNext: 99 },
              transpileModule(source) {
                return {
                  outputText: source
                    .replace(/type\s+\w+\s*=\s*\{[\s\S]*?\};\s*/g, '')
                    .replace(/function(\s+[A-Za-z_$][\w$]*)?\s*\(([^)]*)\)\s*:\s*[A-Za-z_$][\w$]*\s*\{/g, (_match, name = '', parameters) => `function${name}(${stripParams(parameters)}) {`)
                    .replace(/function(\s+[A-Za-z_$][\w$]*)?\s*\(([^)]*)\)/g, (_match, name = '', parameters) => `function${name}(${stripParams(parameters)})`)
                    .replace(/\b(const|let|var)\s+([A-Za-z_$][\w$]*)\s*:\s*[A-Za-z_$][\w$]*/g, '$1 $2')
                };
              }
            };
            """);
    }

    private static SystemPromptCompositionContext PromptContext() => new(
        "/repo",
        new DateOnly(2026, 5, 30),
        PromptMode.Default,
        [],
        [],
        [],
        null,
        null,
        [],
        [],
        new PromptDocumentationPaths("README.md", "docs", "examples"));

    [Fact]
    public async Task SubagentAgentSessionForwardsLifecycleEventsToSubscribers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-subagent-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              try {
                let hasStart = false, hasTurnStart = false, hasMessage = false, hasTurnEnd = false, hasEnd = false;
                const { session } = await createAgentSession();
                session.subscribe(function (event) {
                  if (event.type === 'agent_start') hasStart = true;
                  if (event.type === 'turn_start') hasTurnStart = true;
                  if (event.type === 'message_update') hasMessage = true;
                  if (event.type === 'turn_end') hasTurnEnd = true;
                  if (event.type === 'agent_end') {
                    hasEnd = true;
                    const ok = hasStart && hasTurnStart && hasMessage && hasTurnEnd && hasEnd;
                    pi.registerCommand(`subagent-events-${ok}`, {
                      description: `Subagent events: start=${hasStart} turnStart=${hasTurnStart} message=${hasMessage} turnEnd=${hasTurnEnd} agentEnd=${hasEnd}`,
                      handler: function () { return 'ok'; }
                    });
                  }
                });
                await session.prompt("task");
              } catch (e) {
                pi.registerCommand(`subagent-events-false-${e.message}`, {
                  description: `Subagent events test error: ${e.message}`,
                  handler: function () { return 'ok'; }
                });
              }
            }
            """);
        var registry = new ExtensionRegistry();
        ExtensionRuntimeBinding? binding = null;
        binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) =>
            {
                var sessionId = "child-" + Guid.NewGuid().ToString("N");
                return Task.FromResult<object?>(new
                {
                    ok = true,
                    sessionId,
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child", model = (object?)null },
                    extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                    modelFallbackMessage = (string?)null
                });
            },
            AgentSessionPromptAsync = async (sessionId, content, options, ct) =>
            {
                var cb = binding!.OnChildSessionEventAsync;
                if (cb is not null)
                {
                    await cb(sessionId, new { type = "agent_start" }, ct);
                    await cb(sessionId, new { type = "turn_start" }, ct);
                    await cb(sessionId, new { type = "message_start", message = new { role = "assistant" } }, ct);
                    await cb(sessionId, new { type = "message_update", role = "assistant", text = "result" }, ct);
                    await cb(sessionId, new { type = "message_end", message = new { role = "assistant" } }, ct);
                    await cb(sessionId, new { type = "turn_end" }, ct);
                    await cb(sessionId, new { type = "agent_end", messages = new[] { new { role = "assistant" } } }, ct);
                }
                return new
                {
                    ok = true,
                    sessionId,
                    message = new { role = "assistant" },
                    finalMessage = new { role = "assistant" },
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child" }
                };
            }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        await WaitForCommandAsync(registry, "subagent-events-true", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task PiSubagentsCreateAgentSessionCompatibility()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-pi-subagents-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              const { session } = await createAgentSession();
              let turnCount = 0;
              let collectedText = '';
              session.subscribe(function (event) {
                if (event.type === 'turn_end') turnCount++;
                const message = event.message || (event.messages && event.messages[event.messages.length - 1]);
                const content = message && message.content;
                if (Array.isArray(content)) {
                  for (const part of content) {
                    if (part && typeof part.text === 'string') collectedText += part.text;
                  }
                }
                if (typeof event.text === 'string') collectedText += event.text;
                if (event.type === 'agent_end') {
                  const ok = turnCount >= 1 && collectedText.includes('final text');
                  pi.registerCommand(`pi-subagents-compat-${ok}`, {
                    description: `Pi subagents compatibility turnCount=${turnCount} text=${collectedText}`,
                    handler: function () { return 'ok'; }
                  });
                }
              });
              const result = await session.prompt('task');
              const finalMessage = result.finalMessage || result.message || {};
              const finalContent = finalMessage.content || [];
              if (Array.isArray(finalContent)) {
                for (const part of finalContent) {
                  if (part && typeof part.text === 'string') collectedText += part.text;
                }
              }
            }
            """);
        var registry = new ExtensionRegistry();
        ExtensionRuntimeBinding? binding = null;
        binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) =>
            {
                const string sessionId = "child-1";
                return Task.FromResult<object?>(new
                {
                    ok = true,
                    sessionId,
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child", model = (object?)null },
                    extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                    modelFallbackMessage = (string?)null
                });
            },
            AgentSessionPromptAsync = async (sessionId, content, options, ct) =>
            {
                var finalMessage = new { role = "assistant", content = new[] { new { type = "text", text = "final text" } } };
                var cb = binding!.OnChildSessionEventAsync;
                if (cb is not null)
                {
                    await cb(sessionId, new { type = "turn_start" }, ct);
                    await cb(sessionId, new { type = "message_update", message = finalMessage }, ct);
                    await cb(sessionId, new { type = "turn_end", message = finalMessage }, ct);
                    await cb(sessionId, new { type = "agent_end", messages = new[] { finalMessage } }, ct);
                }
                return new
                {
                    ok = true,
                    sessionId,
                    message = finalMessage,
                    finalMessage,
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child" }
                };
            }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        await WaitForCommandAsync(registry, "pi-subagents-compat-true", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AgentSessionMessagesExposeSnapshotMessagesForFallbackOutput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-subagent-messages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            function extractText(content) {
              if (!Array.isArray(content)) return '';
              return content.map(function (part) { return part && typeof part.text === 'string' ? part.text : ''; }).join('');
            }
            export default async function activate(pi) {
              const { session } = await createAgentSession();
              await session.prompt('task');
              let fallbackText = '';
              for (let i = session.messages.length - 1; i >= 0; i--) {
                const message = session.messages[i];
                if (message.role !== 'assistant') continue;
                fallbackText = extractText(message.content).trim();
                if (fallbackText) break;
              }
              pi.registerCommand(`subagent-message-fallback-${fallbackText === 'final text'}`, {
                description: `Subagent message fallback text=${fallbackText}`,
                handler: function () { return 'ok'; }
              });
            }
            """);
        var registry = new ExtensionRegistry();
        const string sessionId = "child-1";
        var finalMessage = new { role = "assistant", content = new[] { new { type = "text", text = "final text" } } };
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) => Task.FromResult<object?>(new
            {
                ok = true,
                sessionId,
                session = new { sessionId, messages = Array.Empty<object>(), entries = Array.Empty<object>(), sessionName = "child", model = (object?)null },
                extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                modelFallbackMessage = (string?)null
            }),
            AgentSessionPromptAsync = (_, _, _, _) => Task.FromResult<object?>(new
            {
                ok = true,
                sessionId,
                message = finalMessage,
                finalMessage,
                session = new { sessionId, messages = new[] { finalMessage }, entries = Array.Empty<object>(), sessionName = "child" }
            })
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "subagent-message-fallback-true");
    }

    [Fact]
    public async Task SubagentAgentSessionListenerSurvivesLaterExtensionLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-subagent-shared-listeners-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var firstPath = Path.Combine(dir, "first.ts");
        var secondPath = Path.Combine(dir, "second.ts");
        await File.WriteAllTextAsync(firstPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              const { session } = await createAgentSession();
              session.subscribe(function (event) {
                if (event.type === 'agent_start') {
                  pi.registerCommand('first-subagent-listener-received', {
                    description: 'First listener received subagent event after another extension loaded',
                    handler: function () { return 'ok'; }
                  });
                }
              });
            }
            """);
        await File.WriteAllTextAsync(secondPath, """
            export default function activate(pi) {
              pi.registerCommand('second-extension-loaded', { description: 'Second extension loaded', handler: function () { return 'ok'; } });
            }
            """);
        var registry = new ExtensionRegistry();
        var childSessionIds = new List<string>();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) =>
            {
                var sessionId = "child-" + Guid.NewGuid().ToString("N");
                childSessionIds.Add(sessionId);
                return Task.FromResult<object?>(new
                {
                    ok = true,
                    sessionId,
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child", model = (object?)null },
                    extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                    modelFallbackMessage = (string?)null
                });
            }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var first = await host.LoadAsync(firstPath, binding, CancellationToken.None);
        var second = await host.LoadAsync(secondPath, binding, CancellationToken.None);
        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.Contains(registry.Commands, command => command.Value.Name == "second-extension-loaded");
        if (binding.OnChildSessionEventAsync is not null)
            await binding.OnChildSessionEventAsync(Assert.Single(childSessionIds), new { type = "agent_start" }, CancellationToken.None);

        await WaitForCommandAsync(registry, "first-subagent-listener-received", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ResetExtensionsClearsSubagentAgentSessionListeners()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-subagent-reset-listeners-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              const { session } = await createAgentSession();
              session.subscribe(function (event) {
                if (event.type === 'agent_start') {
                  pi.registerCommand('stale-subagent-listener-fired', {
                    description: 'Stale listener fired after reset',
                    handler: function () { return 'bad'; }
                  });
                }
              });
            }
            """);
        var registry = new ExtensionRegistry();
        var childSessionIds = new List<string>();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) =>
            {
                var sessionId = "child-" + Guid.NewGuid().ToString("N");
                childSessionIds.Add(sessionId);
                return Task.FromResult<object?>(new
                {
                    ok = true,
                    sessionId,
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child", model = (object?)null },
                    extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                    modelFallbackMessage = (string?)null
                });
            }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);
        await host.ResetExtensionsAsync(CancellationToken.None);

        if (binding.OnChildSessionEventAsync is not null)
            await binding.OnChildSessionEventAsync(Assert.Single(childSessionIds), new { type = "agent_start" }, CancellationToken.None);
        Assert.DoesNotContain(registry.Commands, command => command.Value.Name == "stale-subagent-listener-fired");
    }

    [Fact]
    public async Task SubagentAgentSessionPromptDoesNotWaitForForwardedLifecycleEventSubscribers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-subagent-event-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              let sawEvent = false;
              const { session } = await createAgentSession();
              session.subscribe(async function (event) {
                if (event.type === 'agent_start') {
                  await new Promise(function (resolve) { setTimeout(resolve, 1000); });
                  sawEvent = true;
                }
              });
              await session.prompt('task');
              pi.registerCommand(`subagent-prompt-did-not-await-event-${!sawEvent}`, {
                description: 'Prompt resolved without waiting for forwarded child event dispatch',
                handler: function () { return 'ok'; }
              });
            }
            """);
        var registry = new ExtensionRegistry();
        ExtensionRuntimeBinding? binding = null;
        binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) =>
            {
                var sessionId = "child-" + Guid.NewGuid().ToString("N");
                return Task.FromResult<object?>(new
                {
                    ok = true,
                    sessionId,
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child", model = (object?)null },
                    extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                    modelFallbackMessage = (string?)null
                });
            },
            AgentSessionPromptAsync = async (sessionId, content, options, ct) =>
            {
                if (binding!.OnChildSessionEventAsync is not null)
                    await binding.OnChildSessionEventAsync(sessionId, new { type = "agent_start" }, ct);
                return new
                {
                    ok = true,
                    sessionId,
                    message = new { role = "assistant" },
                    finalMessage = new { role = "assistant" },
                    session = new { sessionId, entries = Array.Empty<object>(), sessionName = "child" }
                };
            }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "subagent-prompt-did-not-await-event-true");
    }

    [Fact]
    public async Task ChildSessionEventsAreForwardedInOrderedBatches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-subagent-event-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var runnerPath = Path.Combine(dir, "runner.mjs");
        await File.WriteAllTextAsync(runnerPath, """
            import readline from 'node:readline';
            const rl = readline.createInterface({ input: process.stdin, terminal: false });
            function write(message) { process.stdout.write(`${JSON.stringify(message)}\n`); }
            rl.on('line', function (line) {
              const request = JSON.parse(line);
              if (request.method === 'initialize') {
                write({ jsonrpc: '2.0', id: request.id, result: { ok: true, results: [] } });
                return;
              }

              if (request.method === 'event') {
                const name = request.params?.name;
                const payload = request.params?.payload;
                if (name === 'subagents:session:events') {
                  const events = Array.isArray(payload?.events) ? payload.events : [];
                  console.error(`batch:${payload?.sessionId}:${events.length}:${events.map((event) => event.type).join(',')}`);
                } else if (name === 'subagents:session:event') {
                  console.error(`single:${payload?.sessionId}:${payload?.event?.type}`);
                }

                if (request.id != null) write({ jsonrpc: '2.0', id: request.id, result: { ok: true } });
              }
            });
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(RunnerPath: runnerPath, WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        Assert.NotNull(binding.OnChildSessionEventAsync);
        await binding.OnChildSessionEventAsync!("child-1", new { type = "agent_start" }, CancellationToken.None);
        await binding.OnChildSessionEventAsync!("child-1", new { type = "message_update" }, CancellationToken.None);
        await binding.OnChildSessionEventAsync!("child-1", new { type = "agent_end" }, CancellationToken.None);

        var batchLine = await WaitForStderrLineAsync(host, line => line.StartsWith("batch:child-1:", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        Assert.Equal("batch:child-1:3:agent_start,message_update,agent_end", batchLine);
        Assert.DoesNotContain(host.RecentStandardError, line => line.StartsWith("single:child-1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubagentAgentSessionControlMethodsUseChildRuntimeActions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-subagent-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              const { session } = await createAgentSession();
              await session.steer('steer child');
              await session.followUp('follow child');
              await session.abort();
              await session.compact('compact child');
              await session.setModel({ provider: 'child-provider', id: 'child-model', label: 'Child Model' });
              await session.setThinkingLevel('high');
              await session.dispose();
              pi.registerCommand('subagent-control-methods-ok', {
                description: 'Child AgentSession controls completed',
                handler: function () { return 'ok'; }
              });
            }
            """);
        var calls = new List<string>();
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            CreateAgentSessionAsync = (_, _) => Task.FromResult<object?>(new
            {
                ok = true,
                sessionId = "child-1",
                session = new { sessionId = "child-1", entries = Array.Empty<object>(), sessionName = "child", model = (object?)null },
                extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() },
                modelFallbackMessage = (string?)null
            }),
            AgentSessionSteerAsync = (sessionId, content, ct) =>
            {
                calls.Add($"steer:{sessionId}:{content}");
                return Task.FromResult<object?>(new { ok = true, sessionId });
            },
            AgentSessionFollowUpAsync = (sessionId, content, ct) =>
            {
                calls.Add($"follow:{sessionId}:{content}");
                return Task.FromResult<object?>(new { ok = true, sessionId });
            },
            AgentSessionAbortAsync = (sessionId, ct) =>
            {
                calls.Add($"abort:{sessionId}");
                return Task.FromResult<object?>(new { ok = true, sessionId });
            },
            AgentSessionCompactAsync = (sessionId, instructions, ct) =>
            {
                calls.Add($"compact:{sessionId}:{instructions}");
                return Task.FromResult<object?>(new { ok = true, sessionId });
            },
            AgentSessionSetModelAsync = (sessionId, model, ct) =>
            {
                calls.Add($"model:{sessionId}:{model.Provider}/{model.Id}");
                return Task.FromResult<object?>(new { ok = true, sessionId });
            },
            AgentSessionSetThinkingLevelAsync = (sessionId, level, ct) =>
            {
                calls.Add($"thinking:{sessionId}:{level}");
                return Task.FromResult<object?>(new { ok = true, sessionId });
            },
            AgentSessionDisposeAsync = (sessionId, ct) =>
            {
                calls.Add($"dispose:{sessionId}");
                return Task.FromResult<object?>(new { ok = true, sessionId });
            }
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "subagent-control-methods-ok");
        Assert.Equal([
            "steer:child-1:steer child",
            "follow:child-1:follow child",
            "abort:child-1",
            "compact:child-1:compact child",
            "model:child-1:child-provider/child-model",
            "thinking:child-1:High",
            "dispose:child-1"
        ], calls);
    }

    [Fact]
    public async Task NodeSubagentEventDispatchAwaitsSubscriberPromises()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/PiSharp.TsBridge/Node/src/runnerMain.ts"));
        var source = await File.ReadAllTextAsync(sourcePath);

        Assert.Contains("await Promise.resolve(listener(event))", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextSignalBecomesAbortedAfterBridgeAbort()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-signal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.registerTool({
                name: 'check-signal',
                label: 'Check Signal',
                description: 'Check signal aborted state',
                parameters: { type: 'object', properties: {} },
                execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => ({
                  content: [{ type: 'text', text: String(ctx.signal.aborted) }]
                })
              });
              pi.registerCommand('do-abort', {
                description: 'Trigger abort',
                handler: async (args, ctx) => {
                  await ctx.abort();
                  return 'abort-done';
                }
              });
            }
            """);
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance)
        {
            AbortAsync = _ => Task.CompletedTask,
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        var tool = Assert.Single(registry.Tools).Value;
        using var args = JsonDocument.Parse("{}");

        var result1 = await tool.ExecuteAsync("t1", args.RootElement.Clone(), CancellationToken.None);
        var text1 = Assert.IsType<TextContent>(Assert.Single(result1.Content)).Text;
        Assert.Equal("false", text1);

        await host.InvokeCommandResultAsync(new TsCommandInvokeRequest(extensionPath, "do-abort", string.Empty), CancellationToken.None);

        var result2 = await tool.ExecuteAsync("t2", args.RootElement.Clone(), CancellationToken.None);
        var text2 = Assert.IsType<TextContent>(Assert.Single(result2.Content)).Text;
        Assert.Equal("true", text2);
    }

    [Fact]
    public async Task TypeScriptSendMessageAppendsCustomMessageByDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-sendmsg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
                await pi.sendMessage({
                    customType: "approval-card",
                    content: "Approve?",
                    display: true,
                    details: { requestId: "r-1" }
                });
            }
            """);

        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        string? capturedCustomType = null;
        object? capturedContent = null;
        bool? capturedDisplay = null;
        object? capturedDetails = null;
        var sendMessageCalled = false;

        binding.AppendCustomMessageEntryAsync = (customType, content, display, details, ct) =>
        {
            capturedCustomType = customType;
            capturedContent = content;
            capturedDisplay = display;
            capturedDetails = details;
            return Task.CompletedTask;
        };
        binding.SendMessageAsync = (msg, delivery, trigger, ct) =>
        {
            sendMessageCalled = true;
            return Task.CompletedTask;
        };

        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);
        await host.StartAsync(CancellationToken.None);

        Assert.Equal("approval-card", capturedCustomType);
        Assert.True(capturedContent is JsonElement { ValueKind: JsonValueKind.String } stringContent && stringContent.GetString() == "Approve?");
        Assert.True(capturedDisplay);
        Assert.NotNull(capturedDetails);
        Assert.False(sendMessageCalled);
    }

    private static async Task<TsExtensionHost> CreateHostWithExtensionAsync(ExtensionRegistry registry, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, source);
        var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);
        await host.StartAsync(CancellationToken.None);
        return host;
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private static async Task WaitForCommandAsync(ExtensionRegistry registry, string name, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (registry.Commands.Any(command => command.Value.Name == name)) return;
            await Task.Delay(25);
        }

        Assert.Contains(registry.Commands, command => command.Value.Name == name);
    }

    private static async Task<string> WaitForStderrLineAsync(TsExtensionHost host, Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = host.RecentStandardError.FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(25);
        }

        Assert.True(host.RecentStandardError.Any(predicate), "Expected matching stderr line. Actual stderr: " + string.Join(Environment.NewLine, host.RecentStandardError));
        throw new UnreachableException();
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadLineAsync(cts.Token);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            messages.Add(formatter(state, exception));
        }
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
