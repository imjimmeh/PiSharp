using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;

namespace PiSharp.Extensions.Testing;

public sealed class ExtensionTestFixtureBuilder
{
    private readonly IExtension _extension;
    private string _cwd = "/";
    private bool _hasUi;
    private IExtensionUi _ui = NoExtensionUi.Instance;
    private Func<AgentMessage, CancellationToken, Task>? _sendMessage;

    internal ExtensionTestFixtureBuilder(IExtension extension)
    {
        _extension = extension;
    }

    public ExtensionTestFixtureBuilder WithCwd(string cwd)
    {
        _cwd = cwd;
        return this;
    }

    public ExtensionTestFixtureBuilder WithUi(IExtensionUi ui)
    {
        _hasUi = true;
        _ui = ui;
        return this;
    }

    public ExtensionTestFixtureBuilder WithSendMessage(Func<AgentMessage, CancellationToken, Task> handler)
    {
        _sendMessage = handler;
        return this;
    }

    public async Task<ExtensionTestFixture> BuildAsync(CancellationToken cancellationToken = default)
    {
        var capturedMessages = new List<CapturedMessage>();
        var sendMessage = _sendMessage ?? ((msg, _) =>
        {
            capturedMessages.Add(new CapturedMessage(
                msg, ExtensionMessageDelivery.FollowUp, false, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var actions = new ExtensionRuntimeActions(_cwd, _hasUi, _ui, sendMessage);

        await manager.InitializeAsync(
            FakeExtensionDescriptor.Default, _extension, actions, cancellationToken);

        return new ExtensionTestFixture(_extension, registry, capturedMessages);
    }
}

public sealed class ExtensionTestFixture : IAsyncDisposable
{
    private readonly IExtension _extension;
    private readonly List<CapturedMessage> _capturedMessages;

    internal ExtensionTestFixture(
        IExtension extension,
        ExtensionRegistry registry,
        List<CapturedMessage> capturedMessages)
    {
        _extension = extension;
        Registry = registry;
        _capturedMessages = capturedMessages;
    }

    public static ExtensionTestFixtureBuilder Create(IExtension extension) =>
        new(extension);

    public ExtensionRegistry Registry { get; }

    public IReadOnlyList<CapturedMessage> CapturedMessages => _capturedMessages;

    public IAgentTool GetTool(string name)
    {
        var tool = FindTool(name);
        if (tool is null)
        {
            var available = string.Join(", ", Registry.Tools.Select(r => r.Value.Name));
            throw new InvalidOperationException(
                $"Tool '{name}' not found. Available tools: {available}");
        }
        return tool;
    }

    public IAgentTool? FindTool(string name) =>
        Registry.Tools.FirstOrDefault(r => r.Value.Name == name)?.Value;

    public async Task FireEventAsync(
        string eventName,
        JsonElement payload = default,
        CancellationToken cancellationToken = default)
    {
        var evt = new ExtensionEvent(eventName, null!, payload);
        await DispatchAsync(eventName, evt, cancellationToken);
    }

    public async Task FireSessionShutdownAsync(
        string reason = "dispose",
        CancellationToken cancellationToken = default)
    {
        var harnessEvent = new AgentHarnessEvent.Own(
            new AgentHarnessOwnEvent.SessionShutdown(reason));
        var evt = ExtensionEventMapper.Map(harnessEvent);
        await DispatchAsync(ExtensionEventNames.SessionShutdown, evt, cancellationToken);
    }

    public async Task FireBeforePromptRenderAsync(
        CancellationToken cancellationToken = default)
    {
        var harnessEvent = new AgentHarnessEvent.Own(
            new AgentHarnessOwnEvent.BeforePromptRender(
                Prompt: string.Empty,
                Images: [],
                CompositionContext: new SystemPromptCompositionContext(
                    Cwd: "/",
                    CurrentDate: DateOnly.FromDateTime(DateTime.UtcNow),
                    Mode: PromptMode.Default,
                    Tools: [],
                    SelectedToolNames: [],
                    ExplicitGuidelines: [],
                    CustomPrompt: null,
                    AppendPrompt: null,
                    ContextFiles: [],
                    Skills: [],
                    DocumentationPaths: new PromptDocumentationPaths("README.md", "docs", "examples")),
                Document: new SystemPromptDocument([], []),
                Resources: new { }));
        var evt = ExtensionEventMapper.Map(harnessEvent);
        await DispatchAsync(ExtensionEventNames.BeforePromptRender, evt, cancellationToken);
    }

    public async Task<ExtensionMiddlewareContext> RunBeforeMiddlewareAsync(
        string toolName,
        JsonElement args,
        CancellationToken cancellationToken = default)
    {
        var context = MiddlewareContextBuilder.Before(toolName, args);
        await RunMiddlewareChainAsync(context, cancellationToken);
        return context;
    }

    public async Task<ExtensionMiddlewareContext> RunAfterMiddlewareAsync(
        string toolName,
        JsonElement args,
        bool isError = false,
        string? result = null,
        CancellationToken cancellationToken = default)
    {
        var context = MiddlewareContextBuilder.After(toolName, args, isError);
        await RunMiddlewareChainAsync(context, cancellationToken);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        if (_extension is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }

    private async Task DispatchAsync(string eventName, ExtensionEvent evt, CancellationToken ct)
    {
        foreach (var registration in Registry.HandlersFor(eventName))
            await registration.Value.Handler(evt, ct);
    }

    private async Task RunMiddlewareChainAsync(ExtensionMiddlewareContext context, CancellationToken ct)
    {
        var middlewares = Registry.Middleware.Select(r => r.Value).ToArray();
        if (middlewares.Length == 0) return;

        ExtensionNext terminal = (_, _) => Task.CompletedTask;
        ExtensionNext chain = middlewares.Reverse().Aggregate(
            terminal,
            (next, mw) => (ExtensionNext)((ctx, token) => mw(ctx, next, token)));

        await chain(context, ct);
    }
}
