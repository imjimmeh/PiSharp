using System.Diagnostics;
using System.Text.Json;
using PiSharp.Agent.Serialization;
using PiSharp.Extensions;
using PiSharp.TsBridge.Protocol;
using PiSharp.TsBridge.Shims;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class TsBridgeManifestTests
{
    [Fact]
    public void BridgeManifestContainsCompatibilityShimsAndParitySurfaceCatalog()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();

        Assert.Equal(TsBridgeManifestSchema.CurrentVersion, manifest.SchemaVersion);
        Assert.Contains(manifest.ModuleShims, shim => shim.Specifier == "@pi-ai" && shim.CacheFileName == "pisharp-pi-ai-shim.mjs");
        Assert.Contains(manifest.ModuleShims, shim => shim.Specifier == "@pi-tui" && shim.CacheFileName == "pisharp-pi-tui-shim.mjs");
        Assert.Contains(manifest.ModuleShims, shim => shim.Specifier == "@pi-coding-agent" && shim.CacheFileName == "pisharp-pi-coding-agent-shim.mjs");
        Assert.Contains(manifest.ModuleShims.Single(shim => shim.Specifier == "@pi-ai").Exports, export => export.Name == "Type" && export.Kind == TsBridgeShimExportKinds.Namespace);
        Assert.Contains(manifest.ModuleShims.Single(shim => shim.Specifier == "@pi-tui").Exports, export => export.Name == "Text" && export.Helper == "Text");
        Assert.DoesNotContain(manifest.ModuleShims, shim => Path.IsPathRooted(shim.CacheFileName) || shim.CacheFileName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."));

        Assert.Equal(TsBridgeMethods.RegisterCommand, manifest.Protocol.Methods[nameof(TsBridgeMethods.RegisterCommand)]);
        Assert.Equal(TsBridgeRuntimeActions.GetCommands, manifest.Protocol.RuntimeActions[nameof(TsBridgeRuntimeActions.GetCommands)]);
        AssertManifestMember(manifest, "pi", "getCommands", TsBridgeApiMemberStatuses.Snapshot);
        AssertManifestMember(manifest, "pi", "setSessionName", TsBridgeApiMemberStatuses.RuntimeAction);
        AssertManifestMember(manifest, "ctx", "newSession", TsBridgeApiMemberStatuses.RuntimeAction);
        AssertManifestMember(manifest, "replacementCtx", "sendUserMessage", TsBridgeApiMemberStatuses.RuntimeAction);
        AssertManifestMember(manifest, "ctx.sessionManager", "getEntries", TsBridgeApiMemberStatuses.Snapshot);
        AssertManifestMember(manifest, "ctx", "fork", TsBridgeApiMemberStatuses.RuntimeAction);
        AssertManifestMember(manifest, "pi", "exec", TsBridgeApiMemberStatuses.RuntimeAction);
        Assert.Contains(manifest.EventsOrEmpty(), evt => evt.Name == "tool_result" && evt.Status == TsBridgeApiMemberStatuses.Implemented);
    }

    [Fact]
    public void ManifestDeclaresCtxUiCustomWhenInteractiveBridgeIsImplemented()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();

        AssertManifestMember(manifest, "ctx.ui", "custom", TsBridgeApiMemberStatuses.Implemented);
    }

    [Fact]
    public void ManifestDoesNotDeclareCtxUiToolsExpandedWithoutEndToEndSupport()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();

        Assert.DoesNotContain(
            manifest.ApiSurface.Members,
            member => member.Surface == "ctx.ui"
                      && member.Name == "toolsExpanded"
                      && member.Status == TsBridgeApiMemberStatuses.Implemented);
    }

    [Fact]
    public void ManifestDeclaresCtxUiRegisterMenuItem()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();

        AssertManifestMember(manifest, "ctx.ui", "registerMenuItem", TsBridgeApiMemberStatuses.Implemented);
    }

    [Fact]
    public void BridgeManifestDoesNotContainRoadmapOrFalseUnsupportedStatuses()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "TsBridgeManifestFactory.cs"));
        var manifest = TsBridgeManifestFactory.CreateDefault();

        Assert.DoesNotContain("Plan" + "ned(", source);
        Assert.DoesNotContain("not-yet" + "-supported", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(manifest.ApiSurface.Members, member => member.Status == "not-yet" + "-supported");
        Assert.DoesNotContain(manifest.ApiSurface.Events, evt => evt.Status == "not-yet" + "-supported");
        Assert.DoesNotContain(manifest.ApiSurface.Members, member => !string.IsNullOrWhiteSpace(member.UnsupportedReason));
        Assert.DoesNotContain(manifest.ApiSurface.Members, member => !string.IsNullOrWhiteSpace(member.OwnerPhase));
        Assert.DoesNotContain(manifest.ApiSurface.Events, evt => !string.IsNullOrWhiteSpace(evt.OwnerPhase));
        foreach (var member in manifest.ApiSurface.Members.Where(member => member.Status == TsBridgeApiMemberStatuses.RuntimeAction))
        {
            Assert.True(
                manifest.Protocol.RuntimeActions.Values.Contains(member.RuntimeAction!) || manifest.Protocol.Methods.Values.Contains(member.RuntimeAction!),
                $"Runtime member {member.Surface}.{member.Name} references unregistered action {member.RuntimeAction}.");
        }
    }

    [Fact]
    public void BridgeManifestSerializesWithStableCamelCaseContract()
    {
        var json = AgentJsonSerializer.Serialize(TsBridgeManifestFactory.CreateDefault());
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion));
        Assert.Equal(1, schemaVersion.GetInt32());
        Assert.True(document.RootElement.TryGetProperty("moduleShims", out var moduleShims));
        Assert.True(moduleShims.GetArrayLength() >= 9);
        Assert.Contains("\"specifier\":\"@pi-ai\"", json);
        Assert.Contains("\"apiSurface\"", json);
        Assert.Contains("\"getCommands\"", json);
    }

    [Fact]
    public async Task ManifestGeneratedShimsPreservePiAiAndPiTuiRuntimeBehavior()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-shim-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { Type, StringEnum, getSupportedThinkingLevels, completeSimple } from '@pi-ai';
            import { Text, Container, visibleWidth, truncateToWidth } from '@pi-tui';
            export default async function activate(pi) {
              const schema = Type.Object({ name: Type.String({ description: 'x' }), optionalName: Type.Optional(Type.String()) });
              const enumSchema = StringEnum(['a', 'b']);
              const levels = getSupportedThinkingLevels();
              const width = visibleWidth('\x1b[31mred\x1b[39m');
              const truncated = truncateToWidth('abcdef', 3);
              const rendered = new Container([new Text('a'), new Text('b')]).render().join('');
              const ok = schema.required.includes('name') && !schema.required.includes('optionalName') && enumSchema.enum[1] === 'b' && levels.includes('xhigh') && width === 3 && truncated === 'abc' && rendered === 'ab' && typeof completeSimple === 'function';
              pi.registerCommand(`shim-compat-${ok}`, { description: 'Shim compatibility', handler: () => 'ok' });
            }
            """);

        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "shim-compat-true");
    }

    [Fact]
    public async Task ManifestGeneratedShimsSupportLegacyAndScopedAliases()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-shim-aliases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { StringEnum as ShortEnum } from '@pi-ai';
            import { StringEnum as LegacyEnum } from '@mariozechner/pi-ai';
            import { Text as ShortText } from '@pi-tui';
            import { Text as ScopedText } from '@earendil-works/pi-tui';
            import { defineTool } from '@pi-coding-agent';
            export default function activate(pi) {
              const ok = ShortEnum(['short']).enum[0] === 'short' && LegacyEnum(['legacy']).enum[0] === 'legacy' && new ShortText('a').render()[0] === 'a' && new ScopedText('b').render()[0] === 'b' && defineTool({ name: 'tool' }).name === 'tool';
              pi.registerCommand(`shim-alias-${ok}`, { description: 'Shim aliases', handler: () => 'ok' });
            }
            """);

        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "shim-alias-true");
    }

    [Fact]
    public async Task ManifestGeneratedShimsPreservePiCodingAgentRuntimeBehavior()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-shim-coding-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES, defineTool, formatSize, parseFrontmatter, stripFrontmatter, truncateHead, truncateTail } from '@pi-coding-agent';
            export default function activate(pi) {
              const parsed = parseFrontmatter('---\ntitle: Demo\ncount: 2\n---\nBody');
              const tool = defineTool({ name: 'demo' });
              const ok = DEFAULT_MAX_BYTES === 100000 && DEFAULT_MAX_LINES === 2000 && tool.name === 'demo' && parsed.frontmatter.title === 'Demo' && parsed.frontmatter.count === 2 && stripFrontmatter('---\ntitle: Demo\n---\nBody') === 'Body' && truncateHead('abcdef', 3) === 'abc' && truncateTail('abcdef', 3) === 'def' && formatSize(2048) === '2.0 KB';
              pi.registerCommand(`shim-coding-agent-${ok}`, { description: 'Coding agent shim compatibility', handler: () => 'ok' });
            }
            """);

        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "shim-coding-agent-true");
    }

    [Fact]
    public async Task ManifestGeneratedPiCodingAgentCreateAgentSessionReturnsSessionShape()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-shim-create-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import { createAgentSession } from '@pi-coding-agent';
            export default async function activate(pi) {
              const created = await createAgentSession();
              const ok = created && created.session && typeof created.session.prompt === 'function' && typeof created.session.abort === 'function' && Array.isArray(created.extensionsResult.extensions);
              pi.registerCommand(`shim-create-agent-session-${ok}`, { description: 'createAgentSession compatibility', handler: () => 'ok' });
            }
            """);

        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        binding.CreateAgentSessionAsync = async (options, _) =>
        {
            await Task.CompletedTask;
            return new { ok = true, sessionId = $"child-{Guid.NewGuid():N}", session = new { sessionId = "stub", entries = Array.Empty<object>() }, extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() }, modelFallbackMessage = (string?)null };
        };
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "shim-create-agent-session-true");
    }

    [Fact]
    public async Task ManifestGeneratedShimsUseConfiguredBridgeCacheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-shim-cache-dir-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);

        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir, CacheDirectory: cacheDir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(
            Directory.EnumerateFiles(cacheDir, "pisharp-pi-coding-agent-shim*.mjs"),
            file => Path.GetFileName(file).StartsWith("pisharp-pi-coding-agent-shim.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SdkShimRuntimeActionsRouteThroughHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-sdk-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              const bridge = globalThis.__pisharpTsBridge;
              const tools = await bridge.runtime('sdk.createCodingTools', {});
              const theme = await bridge.runtime('sdk.getMarkdownTheme', {});
              const unknown = await bridge.runtime('sdk.nonexistentExport', {});
              const ok = Array.isArray(tools.value) && tools.value.length === 0 && theme.ok === true && theme.value && !Array.isArray(theme.value) && unknown.ok === false && unknown.error.includes('nonexistentExport');
              pi.registerCommand(`sdk-runtime-host-${ok}`, { description: 'SDK runtime routing', handler: () => 'ok' });
            }
            """);

        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "sdk-runtime-host-true");
    }

    [Fact]
    public async Task ManifestGeneratedPiCodingAgentShimImportsEveryValueExportAndThrowsForUnsupportedExport()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();
        var shim = manifest.ModuleShims.Single(s => s.Specifier == "@pi-coding-agent");
        var exportNames = shim.Exports.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var unsupportedExport = SdkShimExportClassification.All.First(kvp => kvp.Value.Status == SdkShimExportStatus.Unsupported).Key;
        var importList = string.Join(", ", exportNames);
        var definedChecks = string.Join(" && ", exportNames.Select(name => $"typeof {name} !== 'undefined'"));

        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-shim-all-coding-agent-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(dir);
        await InstallFakeTypeScriptCompilerAsync(dir);
        var extensionPath = Path.Combine(dir, "extension.ts");
        await File.WriteAllTextAsync(extensionPath, $$"""
            import { {{importList}} } from '@pi-coding-agent';
            export default function activate(pi) {
              let unsupportedOk = false;
              try {
                {{unsupportedExport}}('test');
              } catch (error) {
                unsupportedOk = error instanceof Error && error.message.includes('{{unsupportedExport}}') && error.message.includes('not implemented by PiSharp');
              }
              const ok = {{definedChecks}} && unsupportedOk;
              pi.registerCommand(`shim-all-coding-agent-exports-${ok}`, { description: 'All coding-agent generated exports import', handler: () => 'ok' });
            }
            """);

        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir, CacheDirectory: cacheDir), registry);

        await host.StartAsync(CancellationToken.None);

        Assert.Contains(registry.Commands, command => command.Value.Name == "shim-all-coding-agent-exports-true");
    }

    [Fact]
    public void PiCodingAgentShimAccountsForEveryDiscoveredValueExport()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();
        var shim = manifest.ModuleShims.Single(s => s.Specifier == "@pi-coding-agent");

        var shimExportNames = shim.Exports.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal);
        var classificationKeys = SdkShimExportClassification.All.Keys.OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(classificationKeys, shimExportNames);
        Assert.DoesNotContain(SdkShimExportClassification.All.Values, c => c.Status == SdkShimExportStatus.Unclassified);
    }

    [Fact]
    public void UnsupportedPiCodingAgentShimExportsUseThrowingUnavailableFunctions()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();
        var shim = manifest.ModuleShims.Single(s => s.Specifier == "@pi-coding-agent");

        var unsupportedExportNames = SdkShimExportClassification.All
            .Where(kvp => kvp.Value.Status == SdkShimExportStatus.Unsupported)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var export in shim.Exports.Where(e => unsupportedExportNames.Contains(e.Name)))
        {
            Assert.Equal(TsBridgeShimExportKinds.UnavailableFunction, export.Kind);
            Assert.NotNull(export.Message);
            Assert.Contains(export.Name, export.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BridgeRunnerDoesNotContainInlineLegacyCompatibilityShims()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Node");
        var source = File.ReadAllText(Path.Combine(root, "TsBridgeRunner.mjs"));

        Assert.True(source.Split('\n').Length <= 5, "TsBridgeRunner.mjs should remain a tiny bootstrap; implementation belongs in Node/src/**/*.ts.");
        Assert.True(File.Exists(Path.Combine(root, "src", "runnerMain.ts")), "The bridge runner implementation should be TypeScript.");

        Assert.DoesNotContain("legacyEnsurePi", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pisharp-pi-ai-shim.mjs\");\n\tconst content = `", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pisharp-pi-tui-shim.mjs\");\n\tconst content = `", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeApiCodeUsesBridgeManifestProtocolNames()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Node");
        var piApiSource = File.ReadAllText(Path.Combine(root, "src", "runner", "piApi.ts"));
        var runnerSource = File.ReadAllText(Path.Combine(root, "TsBridgeRunner.mjs"));

        Assert.DoesNotContain("sendRequest(\"runtime_action\"", piApiSource, StringComparison.Ordinal);
        Assert.DoesNotContain("trackRegistrationRequest(\"register_", piApiSource, StringComparison.Ordinal);
        Assert.DoesNotContain("sendRequest(\"runtime_action\"", runnerSource, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "runner")), "Node/runner/*.mjs should stay migrated to Node/src/runner/*.ts plus generated Node/dist/runner/*.js.");
    }

    [Fact]
    public void RuntimeActionsOptOutOfGenericJsonRpcRequestTimeout()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Node");
        var typesSource = File.ReadAllText(Path.Combine(root, "src", "types.ts"));
        var transportSource = File.ReadAllText(Path.Combine(root, "src", "runner", "jsonRpcTransport.ts"));
        var runnerSource = File.ReadAllText(Path.Combine(root, "src", "runnerMain.ts"));
        var piApiSource = File.ReadAllText(Path.Combine(root, "src", "runner", "piApi.ts"));

        Assert.Contains("timeoutMs?: number | null", typesSource, StringComparison.Ordinal);
        Assert.Contains("options?: SendRequestOptions", typesSource, StringComparison.Ordinal);
        Assert.Contains("timeout?: NodeJS.Timeout", typesSource, StringComparison.Ordinal);
        Assert.Contains("options?: SendRequestOptions", transportSource, StringComparison.Ordinal);
        Assert.Contains("{ timeoutMs: null }", runnerSource, StringComparison.Ordinal);
        Assert.Contains("{ timeoutMs: null }", piApiSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeManifestContainsSubagentSessionRuntimeActions()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();

        Assert.Contains("create_agent_session", manifest.Protocol.RuntimeActions.Values);
        Assert.Contains("agent_session_prompt", manifest.Protocol.RuntimeActions.Values);
        Assert.Contains("agent_session_abort", manifest.Protocol.RuntimeActions.Values);
        Assert.Contains("agent_session_dispose", manifest.Protocol.RuntimeActions.Values);
    }

    [Fact]
    public void SdkShimRuntimeDispatcherClassifiesAndRoutesActions()
    {
        var dispatcher = new SdkShimRuntimeDispatcher();

        Assert.True(SdkShimRuntimeDispatcher.CanHandle("sdk.createAgentSession"));
        Assert.True(SdkShimRuntimeDispatcher.CanHandle("sdk.getMarkdownTheme"));
        Assert.False(SdkShimRuntimeDispatcher.CanHandle("get_commands"));
        Assert.False(SdkShimRuntimeDispatcher.CanHandle("prompt_and_wait"));

        var createResult = dispatcher.TryResolve("sdk.createAgentSession", out var mappedAction);
        Assert.Null(createResult);
        Assert.Equal(TsBridgeRuntimeActions.CreateAgentSession, mappedAction);

        var stubResult = dispatcher.TryResolve("sdk.getMarkdownTheme", out _);
        Assert.NotNull(stubResult);
        Assert.True(stubResult.Ok);
        Assert.NotNull(stubResult.Value);

        var unknownResult = dispatcher.TryResolve("sdk.nonexistentExport", out _);
        Assert.NotNull(unknownResult);
        Assert.False(unknownResult.Ok);
        Assert.Contains("nonexistentExport", unknownResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkShimRuntimeDispatcherRejectsUnclassifiedActions()
    {
        var dispatcher = new SdkShimRuntimeDispatcher();

        foreach (var kvp in SdkShimExportClassification.All)
        {
            var action = $"sdk.{kvp.Key}";
            Assert.True(SdkShimRuntimeDispatcher.CanHandle(action), $"Expected CanHandle true for {action}");

            if (kvp.Value.Status == SdkShimExportStatus.Unsupported)
            {
                var result = dispatcher.TryResolve(action, out _);
                Assert.NotNull(result);
                Assert.False(result!.Ok, $"Expected !Ok for unsupported export {action}");
                Assert.Contains(kvp.Key, result.Error, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task AgentSessionShimUsesRealSubagentRuntimeActions()
    {
        var manifest = TsBridgeManifestFactory.CreateDefault();
        var shim = manifest.ModuleShims.Single(s => s.Specifier == "@pi-coding-agent");
        var shimJson = AgentJsonSerializer.Serialize(new
        {
            schemaVersion = manifest.SchemaVersion,
            moduleShims = new[] { new { shim.Specifier, shim.CacheFileName, shim.Exports } }
        });

        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Node");
        var generatorPath = Path.Combine(root, "dist", "shims", "codegen.js");
        Assert.True(File.Exists(generatorPath), $"shimGenerator.js not found at {generatorPath}");

        var tmpDir = Path.Combine(Path.GetTempPath(), "pisharp-agent-session-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Combine(tmpDir, "validate.mjs");
        var manifestPath = Path.Combine(tmpDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, shimJson);

        var escapedGeneratorPath = generatorPath.Replace("\\", "\\\\");
        var escapedManifestPath = manifestPath.Replace("\\", "\\\\");

        await File.WriteAllTextAsync(scriptPath, $$"""
            import { pathToFileURL } from 'node:url';
            import { readFileSync } from 'node:fs';
            const { generateModuleShimSource } = await import(pathToFileURL('{{escapedGeneratorPath}}').href);
            const manifest = JSON.parse(readFileSync('{{escapedManifestPath}}', 'utf8'));
            const source = generateModuleShimSource(manifest.moduleShims[0]);

            const errors = [];

            if (!/createAgentSession/.test(source)) errors.push('MISSING createAgentSession in source');
            else {
              if (!/bridge\.runtime\s*\(bridge\.actions\.createAgentSession/.test(source)) errors.push('createAgentSession MISSING bridge.runtime createAgentSession call');
              if (!/new\s+helpers\.AgentSession\s*\(bridge/.test(source)) errors.push('createAgentSession MISSING AgentSession construction from result');
              if (!/result\?\.ok\s*===\s*false/.test(source)) errors.push('createAgentSession MISSING ok===false check');
            }

            const agentMatch = source.match(/\bAgentSession\s*:\s*class\s+AgentSession\s*\{([\s\S]*?)\n\s*\},\s*$/m);
            if (!agentMatch) { errors.push('FAIL AgentSession class not found in generated shim'); }
            else {
              const classBody = agentMatch[1];

              if (!/constructor\s*\(bridge,\s*sessionId,\s*sessionSnapshot\)/.test(classBody)) errors.push('AgentSession constructor MISSING sessionId/sessionSnapshot params');
              if (!/this\._sessionId\s*=\s*sessionId/.test(classBody)) errors.push('AgentSession constructor MISSING _sessionId');
              if (!/get sessionId\(\)\s*\{\s*return this\._sessionId/.test(classBody)) errors.push('AgentSession MISSING sessionId getter');

              if (/completeSimple/.test(classBody)) errors.push('AgentSession STILL uses completeSimple');
              if (/promptAndWait/.test(classBody)) errors.push('AgentSession STILL uses promptAndWait');

              if (!/bridge\.actions\.agentSessionPrompt/.test(classBody)) errors.push('AgentSession.prompt MISSING agentSessionPrompt action');
              if (!/sessionId:\s*this\._sessionId/.test(classBody)) errors.push('AgentSession.prompt MISSING sessionId in payload');

              if (!/bridge\.actions\.agentSessionAbort/.test(classBody)) errors.push('AgentSession MISSING agentSessionAbort action');
              if (!/bridge\.actions\.agentSessionSteer/.test(classBody)) errors.push('AgentSession MISSING agentSessionSteer action');
              if (!/bridge\.actions\.agentSessionFollowUp/.test(classBody)) errors.push('AgentSession MISSING agentSessionFollowUp action');
              if (!/bridge\.actions\.agentSessionCompact/.test(classBody)) errors.push('AgentSession MISSING agentSessionCompact action');
              if (!/bridge\.actions\.agentSessionSetModel/.test(classBody)) errors.push('AgentSession MISSING agentSessionSetModel action');
              if (!/bridge\.actions\.agentSessionSetThinkingLevel/.test(classBody)) errors.push('AgentSession MISSING agentSessionSetThinkingLevel action');
              if (!/bridge\.actions\.agentSessionDispose/.test(classBody)) errors.push('AgentSession MISSING agentSessionDispose action');
              if (/bridge\.actions\.sendMessage/.test(classBody)) errors.push('AgentSession STILL routes to parent sendMessage');
              if (/bridge\.actions\.sendUserMessage/.test(classBody)) errors.push('AgentSession STILL routes to parent sendUserMessage');
            }

            if (errors.length) {
                console.log('FAIL ' + errors.join(' | '));
                const match = source.match(/\bAgentSession\s*:\s*class\s+AgentSession\s*\{([\s\S]*?)\n\s*\},\s*$/m);
                if (match) console.log('AgentSession class body excerpt:\n' + match[1].slice(0, 3000));
                process.exit(1);
            }
            console.log('PASS');
            """);

        var psi = new ProcessStartInfo("node", $"\"{scriptPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"Node exited with {process.ExitCode}. stderr: {stderr}. stdout: {stdout}");
        Assert.Contains("PASS", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeFunctionShimExportGeneratesValidBridgeCode()
    {
        var shimJson = AgentJsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            moduleShims = new[]
            {
                new
                {
                    specifier = "@test/runtime-func",
                    cacheFileName = "test-runtime-func.mjs",
                    exports = new[]
                    {
                        new
                        {
                            name = "demoFunc",
                            kind = "runtime-function",
                            runtimeAction = "sdk.demo"
                        }
                    }
                }
            }
        });

        Assert.Contains("\"runtime-function\"", shimJson, StringComparison.Ordinal);
        Assert.Contains("\"sdk.demo\"", shimJson, StringComparison.Ordinal);

        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Node");
        var generatorPath = Path.Combine(root, "dist", "shims", "codegen.js");
        Assert.True(File.Exists(generatorPath), $"shimGenerator.js not found at {generatorPath}");

        var tmpDir = Path.Combine(Path.GetTempPath(), "pisharp-rt-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Combine(tmpDir, "validate.mjs");
        var manifestPath = Path.Combine(tmpDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, shimJson);

        var escapedGeneratorPath = generatorPath.Replace("\\", "\\\\");
        var escapedManifestPath = manifestPath.Replace("\\", "\\\\");

        await File.WriteAllTextAsync(scriptPath, $$"""
            import { pathToFileURL } from 'node:url';
            import { readFileSync } from 'node:fs';
            const { generateModuleShimSource } = await import(pathToFileURL('{{escapedGeneratorPath}}').href);

            const manifest = JSON.parse(readFileSync('{{escapedManifestPath}}', 'utf8'));
            const source = generateModuleShimSource(manifest.moduleShims[0]);
            const exportLine = source.split('\n').find(l => l.startsWith('export async'));
            const errors = [];

            if (!/export\s+async\s+function/.test(exportLine)) errors.push('MISSING export async function');
            if (!/bridge\.runtime\("sdk\.demo",\s*\{\s*args\s*\}\)/.test(exportLine)) errors.push('MISSING bridge.runtime("sdk.demo", { args })');
            if (!/!\s*bridge\?\.runtime/.test(exportLine)) errors.push('MISSING bridge-unavailable guard');
            if (!/result\?\.ok\s*===\s*false/.test(exportLine)) errors.push('MISSING ok===false check');
            if (/\$\$args/.test(exportLine)) errors.push('USES $$args');
            if (!/result\?\.value\s*\?\?\s*result/.test(exportLine)) errors.push('MISSING value??result fallback');

            if (errors.length) {
                console.log('FAIL ' + errors.join(' | '));
                console.log('Generated export line: ' + exportLine);
                process.exit(1);
            }
            console.log('PASS');
            """);

        var psi = new ProcessStartInfo("node", $"\"{scriptPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"Node exited with {process.ExitCode}. stderr: {stderr}. stdout: {stdout}");
        Assert.Contains("PASS", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeShimExportsScriptTracesValueExportsAndSkipsTypes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-analyze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "a.ts"), """
            export function greet(name: string): string { return name; }
            export const MAGIC = 42;
            export class Counter { count = 0; }
            export type Person = { name: string };
            export interface Animal { species: string; }
            """);

        await File.WriteAllTextAsync(Path.Combine(dir, "b.ts"), """
            export const B_VALUE = "bee";
            export function bFunc() { return "B"; }
            """);

        await File.WriteAllTextAsync(Path.Combine(dir, "index.ts"), """
            export { greet, MAGIC, Counter } from "./a.js";
            export * from "./b.js";
            export const INDEX_CONST = "root";
            """);

        var analyzerPath = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Tools", "analyze-shim-exports.mjs");

        var outPath = Path.Combine(dir, "exports.json");
        await RunAnalyzerAsync(analyzerPath, Path.Combine(dir, "index.ts"), outPath);

        var json = await File.ReadAllTextAsync(outPath);
        using var doc = JsonDocument.Parse(json);
        var exports = doc.RootElement.GetProperty("exports");

        var names = exports.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("B_VALUE", names);
        Assert.Contains("Counter", names);
        Assert.Contains("INDEX_CONST", names);
        Assert.Contains("MAGIC", names);
        Assert.Contains("bFunc", names);
        Assert.Contains("greet", names);

        Assert.DoesNotContain("Person", names);
        Assert.DoesNotContain("Animal", names);

        Assert.Equal("function", exports.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "greet").GetProperty("kind").GetString());
        Assert.Equal("const", exports.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "MAGIC").GetProperty("kind").GetString());
        Assert.Equal("class", exports.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Counter").GetProperty("kind").GetString());
        Assert.Equal("const", exports.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "B_VALUE").GetProperty("kind").GetString());
        Assert.Equal("function", exports.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "bFunc").GetProperty("kind").GetString());
        Assert.Equal("const", exports.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "INDEX_CONST").GetProperty("kind").GetString());

        foreach (var exp in exports.EnumerateArray())
        {
            Assert.True(exp.TryGetProperty("sourceModule", out var sm), $"Missing sourceModule for {exp.GetProperty("name")}");
            var smValue = sm.GetString()!;
            Assert.NotEmpty(smValue);
            Assert.EndsWith(".ts", smValue, StringComparison.OrdinalIgnoreCase);
            Assert.False(Path.IsPathRooted(smValue), $"sourceModule '{smValue}' should not be an absolute path.");
            Assert.DoesNotContain("\\", smValue, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnalyzeShimExportsNamedReExportOnlyCollectsSelectedNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-analyze-selective-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "mod.ts"), """
            export const wanted = "keep";
            export const extra = "drop";
            """);

        await File.WriteAllTextAsync(Path.Combine(dir, "index.ts"), """
            export { wanted } from "./mod.js";
            """);

        var analyzerPath = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Tools", "analyze-shim-exports.mjs");

        var outPath = Path.Combine(dir, "exports.json");
        await RunAnalyzerAsync(analyzerPath, Path.Combine(dir, "index.ts"), outPath);

        var json = await File.ReadAllTextAsync(outPath);
        using var doc = JsonDocument.Parse(json);
        var exports = doc.RootElement.GetProperty("exports");

        var names = exports.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("wanted", names);
        Assert.DoesNotContain("extra", names);
        Assert.Single(exports.EnumerateArray());
    }

    [Fact]
    public async Task AnalyzeShimExportsResolvesImportedLocalReExports()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-analyze-imported-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "tools.ts"), "export function createTool() { return {}; }\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "sdk.ts"), "import { createTool } from './tools.js';\nexport { createTool };\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "index.ts"), "export { createTool } from './sdk.js';\n");

        var analyzerPath = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Tools", "analyze-shim-exports.mjs");

        var outPath = Path.Combine(dir, "exports.json");
        await RunAnalyzerAsync(analyzerPath, Path.Combine(dir, "index.ts"), outPath);

        var json = await File.ReadAllTextAsync(outPath);
        using var doc = JsonDocument.Parse(json);
        var export = Assert.Single(doc.RootElement.GetProperty("exports").EnumerateArray());

        Assert.Equal("createTool", export.GetProperty("name").GetString());
        Assert.Equal("function", export.GetProperty("kind").GetString());
        Assert.Equal("tools.ts", export.GetProperty("sourceModule").GetString());
    }

    [Fact]
    public async Task ShimExportGeneratorProducesAutoGeneratedShimExportsAndRuntimeActions()
    {
        var projectRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        var generatorProject = Path.Combine(projectRoot, "src", "PiSharp.TsBridge", "Tools", "ShimExportGenerator", "ShimExportGenerator.csproj");
        var inputJson = Path.Combine(projectRoot, "src", "PiSharp.TsBridge", "Shims", "shim-exports.fallback.json");
        var classificationFile = Path.Combine(projectRoot, "src", "PiSharp.TsBridge", "Shims", "SdkShimExportClassification.cs");
        var runtimeActionsFile = Path.Combine(projectRoot, "src", "PiSharp.TsBridge", "TsBridgeManifestFactory.cs");
        var outDir = Path.Combine(Path.GetTempPath(), "pisharp-gen-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);

        var psi = new ProcessStartInfo("dotnet",
            $"run --project \"{generatorProject}\" -- " +
            $"--input \"{inputJson}\" " +
            $"--classification \"{classificationFile}\" " +
            $"--runtime-actions \"{runtimeActionsFile}\" " +
            $"--out-dir \"{outDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"Generator exited with {process.ExitCode}. stderr: {stderr}. stdout: {stdout}");

        var shimExportsPath = Path.Combine(outDir, "ShimExports.Auto.g.cs");
        var runtimeActionsPath = Path.Combine(outDir, "SdkShimRuntimeActions.Auto.g.cs");

        Assert.True(File.Exists(shimExportsPath), $"Expected {shimExportsPath} to exist.");
        Assert.True(File.Exists(runtimeActionsPath), $"Expected {runtimeActionsPath} to exist.");

        var shimExportsContent = await File.ReadAllTextAsync(shimExportsPath);
        var runtimeActionsContent = await File.ReadAllTextAsync(runtimeActionsPath);

        Assert.Contains("DEFAULT_MAX_BYTES", shimExportsContent, StringComparison.Ordinal);
        Assert.Contains("100_000", shimExportsContent, StringComparison.Ordinal);
        Assert.Contains("defineTool", shimExportsContent, StringComparison.Ordinal);
        Assert.Contains("createCodingTools", shimExportsContent, StringComparison.Ordinal);
        Assert.Contains("PiCodingAgentExports", shimExportsContent, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<TsBridgeShimExport>", shimExportsContent, StringComparison.Ordinal);

        Assert.Contains("CreateAgentSession", runtimeActionsContent, StringComparison.Ordinal);
        Assert.Contains("sdk.createAgentSession", runtimeActionsContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShimExportGeneratorFailsOnUnclassifiedExport()
    {
        var projectRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        var generatorProject = Path.Combine(projectRoot, "src", "PiSharp.TsBridge", "Tools", "ShimExportGenerator", "ShimExportGenerator.csproj");
        var classificationFile = Path.Combine(projectRoot, "src", "PiSharp.TsBridge", "Shims", "SdkShimExportClassification.cs");
        var runtimeActionsFile = Path.Combine(projectRoot, "src", "PiSharp.TsBridge", "TsBridgeManifestFactory.cs");

        var tmpDir = Path.Combine(Path.GetTempPath(), "pisharp-gen-unclassified-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var outDir = Path.Combine(tmpDir, "out");
        Directory.CreateDirectory(outDir);

        var inputJson = Path.Combine(tmpDir, "exports.json");
        await File.WriteAllTextAsync(inputJson, """
            {
              "exports": [
                { "name": "unknownExport", "kind": "function", "sourceModule": "test.ts" }
              ]
            }
            """);

        var psi = new ProcessStartInfo("dotnet",
            $"run --project \"{generatorProject}\" -- " +
            $"--input \"{inputJson}\" " +
            $"--classification \"{classificationFile}\" " +
            $"--runtime-actions \"{runtimeActionsFile}\" " +
            $"--out-dir \"{outDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        var combined = stdout + stderr;
        Assert.Contains("Unclassified SDK export 'unknownExport'", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackShimExportsJsonContainsCurrentRuntimeValueExports()
    {
        var fallbackPath = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "PiSharp.TsBridge", "Shims", "shim-exports.fallback.json");
        Assert.True(File.Exists(fallbackPath),
            $"shim-exports.fallback.json expected at {fallbackPath}; create it from the known export inventory.");

        var json = await File.ReadAllTextAsync(fallbackPath);
        using var doc = JsonDocument.Parse(json);
        var exports = doc.RootElement.GetProperty("exports");

        var names = exports.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("createCodingTools", names);
        Assert.Contains("createReadOnlyTools", names);
        Assert.Contains("defineTool", names);
        Assert.Contains("formatSize", names);

        Assert.DoesNotContain(names, n => n == "ExtensionAPI");

        foreach (var exp in exports.EnumerateArray())
        {
            Assert.True(exp.TryGetProperty("name", out _));
            Assert.True(exp.TryGetProperty("kind", out _));
            Assert.True(exp.TryGetProperty("sourceModule", out _));

            var kind = exp.GetProperty("kind").GetString();
            Assert.Contains(kind, new[] { "function", "class", "const" });
        }
    }

    private static async Task RunAnalyzerAsync(string analyzerPath, string entryPath, string outPath)
    {
        var psi = new ProcessStartInfo("node", $"\"{analyzerPath}\" --entry \"{entryPath}\" --out \"{outPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"Analyzer exited with {process.ExitCode}. stderr: {stderr}. stdout: {stdout}");
    }

    private static void AssertManifestMember(TsBridgeManifest manifest, string surface, string name, string status)
        => Assert.Contains(manifest.ApiSurface.Members, member => member.Surface == surface && member.Name == name && member.Status == status);

    private static async Task InstallFakeTypeScriptCompilerAsync(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "node_modules", "typescript", "lib"));
        await File.WriteAllTextAsync(Path.Combine(directory, "node_modules", "typescript", "package.json"), "{ \"name\": \"typescript\", \"main\": \"lib/typescript.js\" }");
        await File.WriteAllTextAsync(Path.Combine(directory, "node_modules", "typescript", "lib", "typescript.js"), """
            module.exports = {
              version: 'fake-local',
              ModuleKind: { ES2022: 99 },
              ScriptTarget: { ES2022: 99 },
              ModuleResolutionKind: { NodeNext: 99 },
              transpileModule(source) { return { outputText: source }; }
            };
            """);
    }
}

file static class TsBridgeManifestTestExtensions
{
    public static IReadOnlyList<TsBridgeEventContract> EventsOrEmpty(this TsBridgeManifest manifest)
        => manifest.ApiSurface.Events;
}
