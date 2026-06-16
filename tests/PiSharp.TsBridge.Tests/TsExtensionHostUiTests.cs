using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class TsExtensionHostUiTests
{
    [Fact]
    public async Task TypeScriptExtensionSessionStartCanSetStyledLspStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-lsp-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.on('session_start', (_event, ctx) => { if (ctx.hasUI) ctx.ui.setStatus('pi-lens-lsp', ctx.ui.theme.fg('error', 'LSP Inactive')); }); }");
        var requestSource = new TaskCompletionSource<ExtensionUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new ExtensionRuntimeBinding(dir, true, new CapturingUi(requestSource));
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry(), binding);

        await host.StartAsync(CancellationToken.None);
        await host.ForwardEventAsync(new PiSharp.Agent.Core.Events.AgentHarnessEvent.Own(new PiSharp.Agent.Core.Events.AgentHarnessOwnEvent.SessionStart("startup")), CancellationToken.None);
        var request = await requestSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("pi-lens-lsp", request.ExtensionId);
        Assert.Equal("status", request.Kind);
        Assert.Equal("\u001b[31mLSP Inactive\u001b[39m", request.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TypeScriptExtensionUiReadyReplaysSessionStartForUiStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-lsp-ui-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.on('session_start', (_event, ctx) => { if (ctx.hasUI) ctx.ui.setStatus('pi-lens-lsp', ctx.ui.theme.fg('error', 'LSP Inactive')); }); }");
        var requestSource = new TaskCompletionSource<ExtensionUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry(), binding);

        await host.StartAsync(CancellationToken.None);
        await host.ForwardEventAsync(new PiSharp.Agent.Core.Events.AgentHarnessEvent.Own(new PiSharp.Agent.Core.Events.AgentHarnessOwnEvent.SessionStart("startup")), CancellationToken.None);
        binding.SetUi(new CapturingUi(requestSource), true);
        await host.SetRuntimeHasUiAsync(true, CancellationToken.None);
        var request = await requestSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("pi-lens-lsp", request.ExtensionId);
        Assert.Equal("status", request.Kind);
        Assert.Equal("\u001b[31mLSP Inactive\u001b[39m", request.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TypeScriptExtensionLoadedAfterUiReadyRunsSessionStartForUiStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-lsp-late-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.on('session_start', (_event, ctx) => { if (ctx.hasUI) ctx.ui.setStatus('pi-lens-lsp', ctx.ui.theme.fg('error', 'LSP Inactive')); }); }");
        var requestSource = new TaskCompletionSource<ExtensionUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new ExtensionRuntimeBinding(dir, false, NoExtensionUi.Instance);
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: dir), new ExtensionRegistry(), binding);

        await host.StartAsync(CancellationToken.None);
        binding.SetUi(new CapturingUi(requestSource), true);
        await host.SetRuntimeHasUiAsync(true, CancellationToken.None);
        await host.LoadAsync(extensionPath, binding, CancellationToken.None);
        var request = await requestSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("pi-lens-lsp", request.ExtensionId);
        Assert.Equal("status", request.Kind);
        Assert.Equal("\u001b[31mLSP Inactive\u001b[39m", request.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TypeScriptExtensionSessionStartSeesUiAndCanSetWidget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-widget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.on('session_start', (_event, ctx) => { if (ctx.hasUI) ctx.ui.setWidget('rpiv-todos', () => ({ render: () => ['Todos overlay'] }), { placement: 'aboveEditor' }); }); }");
        var requestSource = new TaskCompletionSource<ExtensionUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new ExtensionRuntimeBinding(dir, true, new CapturingUi(requestSource));
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry(), binding);

        await host.StartAsync(CancellationToken.None);
        await host.ForwardEventAsync(new PiSharp.Agent.Core.Events.AgentHarnessEvent.Own(new PiSharp.Agent.Core.Events.AgentHarnessOwnEvent.SessionStart("startup")), CancellationToken.None);
        var request = await requestSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("rpiv-todos", request.ExtensionId);
        Assert.Equal("widget", request.Kind);
        Assert.Equal("Todos overlay", request.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TypeScriptExtensionSetWidgetReceivesTerminalDimensions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-widget-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.on('session_start', (_event, ctx) => { if (ctx.hasUI) ctx.ui.setWidget('agent-widget', (tui) => ({ render: () => ['columns ' + tui.terminal.columns + ' rows ' + tui.terminal.rows] }), { placement: 'aboveEditor' }); }); }");
        var requestSource = new TaskCompletionSource<ExtensionUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new ExtensionRuntimeBinding(dir, true, new CapturingUi(requestSource));
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry(), binding);

        await host.StartAsync(CancellationToken.None);
        await host.ForwardEventAsync(new PiSharp.Agent.Core.Events.AgentHarnessEvent.Own(new PiSharp.Agent.Core.Events.AgentHarnessOwnEvent.SessionStart("startup")), CancellationToken.None);
        var request = await requestSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("agent-widget", request.ExtensionId);
        Assert.Equal("widget", request.Kind);
        Assert.Equal("columns 80 rows 24", request.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TypeScriptExtensionSetFooterSendsRenderedFooterIntent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-footer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.ui.setFooter(() => ({ render: () => ['ts footer'] })); }");
        var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry());
        var requestSource = new TaskCompletionSource<TsUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SetUiBridge(request =>
        {
            requestSource.TrySetResult(request);
            return Task.FromResult<object?>(new TsUiResponse(request.RequestId, true, false));
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var request = await requestSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("footer", request.Kind);
            Assert.Equal("ts footer", request.Message);
        }
    }

    [Fact]
    public async Task TypeScriptExtensionThemePreservesAnsiStylingInFooter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-footer-style-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.ui.setFooter((_tui, theme) => ({ render: () => [theme.fg('success', 'LSP') + ' ' + theme.fg('error', 'Inactive')] })); }");
        var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry());
        var footerSource = new TaskCompletionSource<TsUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SetUiBridge(request =>
        {
            if (request.Kind == "footer") footerSource.TrySetResult(request);
            return Task.FromResult<object?>(new TsUiResponse(request.RequestId, true, false));
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var footer = await footerSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("\u001b[32mLSP\u001b[39m \u001b[31mInactive\u001b[39m", footer.Message);
        }
    }

    [Fact]
    public async Task TypeScriptExtensionFooterDataExposesSetStatusValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-footer-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.ui.setStatus('demo', 'ready'); pi.ui.setFooter((_tui, _theme, footerData) => ({ render: () => [footerData.getExtensionStatuses().get('demo') ?? 'missing'] })); }");
        var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry());
        var footerSource = new TaskCompletionSource<TsUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SetUiBridge(request =>
        {
            if (request.Kind == "footer") footerSource.TrySetResult(request);
            return Task.FromResult<object?>(new TsUiResponse(request.RequestId, true, false));
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var footer = await footerSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("ready", footer.Message);
        }
    }

    [Fact]
    public async Task TypeScriptExtensionSetFooterClearsPreviousFooterAndDisposesComponent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-footer-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.ui.setFooter(() => ({ render: () => ['ts footer'], dispose: () => pi.ui.setStatus('disposed', 'yes') })); pi.ui.setFooter(undefined); }");
        var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry());
        var requests = new List<TsUiRequest>();
        var disposedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SetUiBridge(request =>
        {
            requests.Add(request);
            if (request.Kind == "status" && request.ExtensionId == "disposed" && request.Message == "yes") disposedSource.TrySetResult();
            return Task.FromResult<object?>(new TsUiResponse(request.RequestId, true, false));
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            await disposedSource.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(requests, request => request.Kind == "footer" && request.Message == "ts footer");
            Assert.Contains(requests, request => request.Kind == "footer" && request.Message is null);
        }
    }

    [Fact]
    public async Task TypeScriptCustomUiFactoryReceivesInputAndResolvesDoneValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-custom-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerTool({ name: 'custom_ui_tool', label: 'Custom UI', description: 'Interactive custom UI', parameters: { type: 'object', properties: {} }, execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => { const result = await ctx.ui.custom((tui, theme, _kb, done) => { let selected = 'Alpha'; return { render: () => [selected], handleInput: (data) => { if (data === '\\u001b[B') selected = 'Beta'; if (data === '\\r') done({ selected }); tui.requestRender(); } }; }, { overlay: true }); return { content: [{ type: 'text', text: result.selected }] }; } }); }");

        TsExtensionHost? host = null;
        var ui = new ScriptedCustomUi(async request =>
        {
            Assert.Equal("custom", request.Kind);
            Assert.Equal("interactive-component", request.Payload.GetProperty("mode").GetString());
            Assert.True(request.Payload.TryGetProperty("lines", out var lines));
            Assert.True(lines.ValueKind is JsonValueKind.Array);
            Assert.True(request.Payload.TryGetProperty("width", out var width));
            Assert.True(width.GetInt32() > 0);
            Assert.True(request.Payload.TryGetProperty("height", out var height));
            Assert.True(height.GetInt32() > 0);
            Assert.True(request.Payload.GetProperty("overlay").GetBoolean());
            var requestId = request.Payload.GetProperty("requestId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            var moved = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\u001b[B"), CancellationToken.None);
            Assert.Contains("Beta", moved.Lines);
            Assert.False(moved.Completed);

            var completed = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\r"), CancellationToken.None);
            Assert.True(completed.Completed);
            var completedValue = Assert.IsType<JsonElement>(completed.Value);
            Assert.Equal("Beta", completedValue.GetProperty("selected").GetString());

            return new ExtensionUiResult(true, completed.Value);
        });

        var binding = new ExtensionRuntimeBinding(dir, true, ui);
        var registry = new ExtensionRegistry();
        host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var tool = Assert.Single(registry.Tools).Value;
            using var args = JsonDocument.Parse("{}");
            var result = await tool.ExecuteAsync("tool-1", args.RootElement.Clone(), CancellationToken.None);

            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            Assert.Equal("Beta", text);
        }
    }

    [Fact]
    public async Task TypeScriptCustomUiInputExceptionCompletesSessionWithErrorSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-custom-ui-input-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerTool({ name: 'custom_ui_tool', label: 'Custom UI', description: 'Interactive custom UI', parameters: { type: 'object', properties: {} }, execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => { await ctx.ui.custom(() => ({ render: () => ['Pick'], handleInput: () => { throw new Error('input exploded'); } }), { overlay: true }); return { content: [{ type: 'text', text: 'done' }] }; } }); }");

        TsExtensionHost? host = null;
        var registry = new ExtensionRegistry();
        host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);
        host.SetUiBridge(async request =>
        {
            if (request.Kind != "custom")
                return new TsUiResponse(request.RequestId, true, false);

            var requestId = request.Payload.GetProperty("requestId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            var errored = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\r"), CancellationToken.None);

            Assert.True(errored.Completed);
            Assert.Contains("input exploded", errored.Error, StringComparison.OrdinalIgnoreCase);

            return new TsUiResponse(request.RequestId, null, false);
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var tool = Assert.Single(registry.Tools).Value;
            using var args = JsonDocument.Parse("{}");
            await tool.ExecuteAsync("tool-1", args.RootElement.Clone(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task TypeScriptCustomUiPreservesNullDoneValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-custom-ui-null-value-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerTool({ name: 'custom_ui_tool', label: 'Custom UI', description: 'Interactive custom UI', parameters: { type: 'object', properties: {} }, execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => { const result = await ctx.ui.custom((_tui, _theme, _kb, done) => ({ render: () => ['Pick'], handleInput: () => done(null) }), { overlay: true }); return { content: [{ type: 'text', text: result === null ? 'null' : JSON.stringify(result) }] }; } }); }");

        TsExtensionHost? host = null;
        var registry = new ExtensionRegistry();
        host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);
        host.SetUiBridge(async request =>
        {
            if (request.Kind != "custom")
                return new TsUiResponse(request.RequestId, true, false);

            var requestId = request.Payload.GetProperty("requestId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            var completed = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\r"), CancellationToken.None);
            Assert.True(completed.Completed);
            Assert.Null(completed.Value);

            return new TsUiResponse(request.RequestId, completed.Value, false);
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var tool = Assert.Single(registry.Tools).Value;
            using var args = JsonDocument.Parse("{}");
            var result = await tool.ExecuteAsync("tool-1", args.RootElement.Clone(), CancellationToken.None);

            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            Assert.Equal("null", text);
        }
    }

    [Fact]
    public async Task CustomUiQuestionnaireFixtureSupportsArrowEnterAndPreviewLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-custom-ui-questionnaire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "custom-ui-questionnaire-extension.mjs");
        var extensionPath = Path.Combine(dir, "extension.mjs");
        File.Copy(fixturePath, extensionPath);

        TsExtensionHost? host = null;
        var ui = new ScriptedCustomUi(async request =>
        {
            Assert.Equal("custom", request.Kind);
            Assert.Equal("interactive-component", request.Payload.GetProperty("mode").GetString());

            var lines = request.Payload.GetProperty("lines").EnumerateArray().Select(line => line.GetString()).Where(line => line is not null).Select(line => line!).ToArray();
            Assert.Contains("# Pick a choice", lines);
            Assert.Contains("> Preview: `markdown-looking` lines stay intact", lines);

            var requestId = request.Payload.GetProperty("requestId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            var moved = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\u001b[B"), CancellationToken.None);
            Assert.Contains("> Beta", moved.Lines);
            Assert.Contains("> Preview: `markdown-looking` lines stay intact", moved.Lines);
            Assert.False(moved.Completed);

            var completed = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\r"), CancellationToken.None);
            Assert.True(completed.Completed);
            var completedValue = Assert.IsType<JsonElement>(completed.Value);
            Assert.Equal("Beta", completedValue.GetProperty("selected").GetString());

            return new ExtensionUiResult(true, completed.Value);
        });

        var binding = new ExtensionRuntimeBinding(dir, true, ui);
        var registry = new ExtensionRegistry();
        host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var tool = Assert.Single(registry.Tools).Value;

            using var args = JsonDocument.Parse("{}");
            var result = await tool.ExecuteAsync("tool-1", args.RootElement.Clone(), CancellationToken.None);

            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            Assert.Equal("Beta", text);
        }
    }

    [Fact]
    public async Task TypeScriptCustomUiInputSurfacesUnknownSessionError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-custom-ui-missing-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('noop', { description: 'noop', handler: () => 'ok' }); }");
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry(), new ExtensionRuntimeBinding(dir, true, new ScriptedCustomUi(_ => throw new NotSupportedException())));

        await host.StartAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendCustomUiInputAsync(new TsCustomUiInputRequest("missing-session", "x"), CancellationToken.None));

        Assert.Contains("unknown custom ui session", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implemented", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendCustomUiInputAsyncSurfacesUnknownSessionError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-custom-ui-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate() {}");
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), new ExtensionRegistry());

        await host.StartAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendCustomUiInputAsync(new TsCustomUiInputRequest("missing", "x"), CancellationToken.None));

        Assert.Contains("unknown custom ui session", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implemented", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomUiRequestRenderUpdatesSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-custom-ui-rerender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerTool({ name: 'custom_ui_tool', label: 'Custom UI', description: 'Interactive custom UI', parameters: { type: 'object', properties: {} }, execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => { let selected = 'Alpha'; const result = await ctx.ui.custom((tui, _theme, _keybindings, done) => ({ render: () => [selected], handleInput: (data) => { if (data === '\\u001b[B') { selected = 'Beta'; tui.requestRender(); } if (data === '\\r') done({ selected }); } }), { overlay: true }); return { content: [{ type: 'text', text: result.selected }] }; } }); }");

        TsExtensionHost? host = null;
        var updateSource = new TaskCompletionSource<TsUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ExtensionRegistry();
        host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);
        host.SetUiBridge(async request =>
        {
            if (request.Kind == "custom_update")
            {
                updateSource.TrySetResult(request);
                return new TsUiResponse(request.RequestId, true, false);
            }

            if (request.Kind != "custom")
                return new TsUiResponse(request.RequestId, true, false);

            var requestId = request.Payload.GetProperty("requestId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            var moved = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\u001b[B"), CancellationToken.None);
            Assert.Contains("Beta", moved.Lines);
            Assert.False(moved.Completed);

            var update = await updateSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("custom_update", update.Kind);
            Assert.Contains("Beta", update.Payload.GetProperty("lines").EnumerateArray().Select(line => line.GetString()).Where(line => line is not null).Select(line => line!));

            var completed = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\r"), CancellationToken.None);
            Assert.True(completed.Completed);
            var completedValue = Assert.IsType<JsonElement>(completed.Value);
            Assert.Equal("Beta", completedValue.GetProperty("selected").GetString());

            return new TsUiResponse(request.RequestId, completed.Value, false);
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var tool = Assert.Single(registry.Tools).Value;

            using var args = JsonDocument.Parse("{}");
            var result = await tool.ExecuteAsync("tool-1", args.RootElement.Clone(), CancellationToken.None);

            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            Assert.Equal("Beta", text);
        }
    }

    [Fact]
    public async Task CustomUiRequestRenderCoalescesRapidRepeatedUpdates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-custom-ui-rerender-coalesce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerTool({ name: 'custom_ui_tool', label: 'Custom UI', description: 'Interactive custom UI', parameters: { type: 'object', properties: {} }, execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => { let selected = 'Alpha'; const result = await ctx.ui.custom((tui, _theme, _keybindings, done) => ({ render: () => [selected], handleInput: (data) => { if (data === '\\u001b[B') { selected = 'Beta'; tui.requestRender(); selected = 'Gamma'; tui.requestRender(); selected = 'Delta'; tui.requestRender(); } if (data === '\\r') done({ selected }); } }), { overlay: true }); return { content: [{ type: 'text', text: result.selected }] }; } }); }");

        TsExtensionHost? host = null;
        var registry = new ExtensionRegistry();
        var firstUpdate = new TaskCompletionSource<TsUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondUpdate = new TaskCompletionSource<TsUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateCount = 0;

        host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry);
        host.SetUiBridge(async request =>
        {
            if (request.Kind == "custom_update")
            {
                var current = Interlocked.Increment(ref updateCount);
                if (current == 1)
                {
                    firstUpdate.TrySetResult(request);
                    await releaseFirstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (current == 2)
                {
                    secondUpdate.TrySetResult(request);
                }
                else
                {
                    Assert.Fail($"Unexpected custom_update request #{current}.");
                }

                return new TsUiResponse(request.RequestId, true, false);
            }

            if (request.Kind != "custom")
                return new TsUiResponse(request.RequestId, true, false);

            var requestId = request.Payload.GetProperty("requestId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            var moved = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\u001b[B"), CancellationToken.None);
            Assert.Contains("Delta", moved.Lines);
            Assert.False(moved.Completed);

            var first = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref updateCount));
            Assert.Contains("Beta", first.Payload.GetProperty("lines").EnumerateArray().Select(line => line.GetString()).Where(line => line is not null).Select(line => line!));

            releaseFirstUpdate.TrySetResult();

            var second = await secondUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref updateCount));
            Assert.Contains("Delta", second.Payload.GetProperty("lines").EnumerateArray().Select(line => line.GetString()).Where(line => line is not null).Select(line => line!));

            var completed = await host!.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId!, "\r"), CancellationToken.None);
            Assert.True(completed.Completed);
            var completedValue = Assert.IsType<JsonElement>(completed.Value);
            Assert.Equal("Delta", completedValue.GetProperty("selected").GetString());

            return new TsUiResponse(request.RequestId, completed.Value, false);
        });

        await using (host)
        {
            await host.StartAsync(CancellationToken.None);
            var tool = Assert.Single(registry.Tools).Value;

            using var args = JsonDocument.Parse("{}");
            var result = await tool.ExecuteAsync("tool-1", args.RootElement.Clone(), CancellationToken.None);

            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            Assert.Equal("Delta", text);
        }
    }

    [Fact]
    public async Task RegisterShortcutRequestStoresShortcutRegistration()
    {
        var registry = new ExtensionRegistry();
        var host = new TsExtensionHost(new TsBridgeOptions(), registry);
        var method = typeof(TsExtensionHost).GetMethod("RegisterShortcutAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<object?>>(method.Invoke(host, [new { extensionId = "ext", keys = "ctrl+k", description = "Run test" }, CancellationToken.None]));
        await task;

        var shortcut = Assert.Single(registry.Shortcuts);
        Assert.Equal("extension:ts:ext", shortcut.SourceId);
        Assert.Equal("ctrl+k", shortcut.Value.Keys);
        Assert.Equal("Run test", shortcut.Value.Description);
    }

    [Fact]
    public async Task ForwardUiRequestReturnsCancelledWhenUiUnavailable()
    {
        var host = new TsExtensionHost(new TsBridgeOptions(), new ExtensionRegistry());

        var result = await host.ForwardUiRequestAsync(new TsUiRequest("req", "ext", "select", "Pick", null, ["a"], null));

        Assert.True(result.Cancelled);
        Assert.Equal("req", result.RequestId);
    }

    [Fact]
    public async Task ForwardUiRequestUsesConfiguredBridge()
    {
        var host = new TsExtensionHost(new TsBridgeOptions(), new ExtensionRegistry());
        host.SetUiBridge(request => Task.FromResult<object?>(new TsUiResponse(request.RequestId, request.Options?.FirstOrDefault(), false)));

        var result = await host.ForwardUiRequestAsync(new TsUiRequest("req", "ext", "select", "Pick", null, ["a"], null));

        Assert.False(result.Cancelled);
        Assert.Equal("a", result.Value);
    }

    [Fact]
    public async Task ForwardUiRequestUsesConfiguredBridgeForWorkingMessage()
    {
        var host = new TsExtensionHost(new TsBridgeOptions(), new ExtensionRegistry());
        host.SetUiBridge(request => Task.FromResult<object?>(new TsUiResponse(request.RequestId, request.Message, false)));

        var result = await host.ForwardUiRequestAsync(new TsUiRequest("req", "ext", "working_message", "Working", "Crunching", null, null));

        Assert.False(result.Cancelled);
        Assert.Equal("Crunching", result.Value);
    }

    [Fact]
    public async Task TypeScriptUiSelectReturnsSelectedStringForArrayOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-ui-select-array-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('select-test', { description: 'select test', handler: async (_args, ctx) => { const choice = await ctx.ui.select('Agents', ['Running agents (1)']); return choice.startsWith('Running agents (') ? 'ok' : 'bad'; } }); }");
        var binding = new ExtensionRuntimeBinding(dir, true, new ScriptedCustomUi(request => Task.FromResult(new ExtensionUiResult(true, "Running agents (1)"))));
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);
        var result = await host.InvokeCommandResultAsync(new TsCommandInvokeRequest(extensionPath, "select-test", string.Empty), CancellationToken.None);

        Assert.False(result.IsError, result.Message);
        Assert.Equal("ok", result.Message);
    }

    [Fact]
    public async Task TypeScriptUiEditorFunctionReturnsEditedString()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-ts-ui-editor-function-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var extensionPath = Path.Combine(dir, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('editor-test', { description: 'editor test', handler: async (_args, ctx) => { const edited = await ctx.ui.editor('System prompt', 'draft'); return edited === 'edited draft' ? 'ok' : 'bad'; } }); }");
        var binding = new ExtensionRuntimeBinding(dir, true, new ScriptedCustomUi(request => Task.FromResult(new ExtensionUiResult(true, request.Kind == "editor" ? "edited draft" : null))));
        var registry = new ExtensionRegistry();
        await using var host = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: dir), registry, binding);

        await host.StartAsync(CancellationToken.None);
        var result = await host.InvokeCommandResultAsync(new TsCommandInvokeRequest(extensionPath, "editor-test", string.Empty), CancellationToken.None);

        Assert.False(result.IsError, result.Message);
        Assert.Equal("ok", result.Message);
    }

    [Fact]
    public void ContractsSerializeCustomComponentIntent()
    {
        var request = new TsUiRequest("req", "ext", "custom", "Widget", null, null, new { type = "panel" });

        Assert.Equal("custom", request.Kind);
        Assert.NotNull(request.Component);
    }

    [Fact]
    public void ContractsSerializeInteractiveCustomUiPayload()
    {
        var snapshot = new TsCustomUiSnapshot(
            RequestId: "custom-1",
            Lines: ["one", "two"],
            Width: 80,
            Height: 24,
            Completed: false,
            Value: null,
            Error: null);

        var json = PiSharp.Agent.Serialization.AgentJsonSerializer.Serialize(snapshot);
        var roundTripped = PiSharp.Agent.Serialization.AgentJsonSerializer.Deserialize<TsCustomUiSnapshot>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal("custom-1", roundTripped.RequestId);
        Assert.Equal(["one", "two"], roundTripped.Lines);
        Assert.False(roundTripped.Completed);

        var inputRequest = new TsCustomUiInputRequest(
            RequestId: "custom-1",
            Data: "payload",
            Width: 80,
            Height: 24,
            Event: "input");

        var inputJson = PiSharp.Agent.Serialization.AgentJsonSerializer.Serialize(inputRequest);
        var roundTrippedInput = PiSharp.Agent.Serialization.AgentJsonSerializer.Deserialize<TsCustomUiInputRequest>(inputJson);

        Assert.NotNull(roundTrippedInput);
        Assert.Equal("custom-1", roundTrippedInput.RequestId);
        Assert.Equal("payload", roundTrippedInput.Data);
        Assert.Equal(80, roundTrippedInput.Width);
        Assert.Equal(24, roundTrippedInput.Height);
        Assert.Equal("input", roundTrippedInput.Event);
    }

    private sealed class CapturingUi(TaskCompletionSource<ExtensionUiRequest> requestSource) : IExtensionUi
    {
        public Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
        {
            requestSource.TrySetResult(request);
            return Task.FromResult(new ExtensionUiResult(true));
        }

        public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ScriptedCustomUi(Func<ExtensionUiRequest, Task<ExtensionUiResult>> requestHandler) : IExtensionUi
    {
        public Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
            => requestHandler(request);

        public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
