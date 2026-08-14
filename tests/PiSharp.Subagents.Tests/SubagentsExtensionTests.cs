using PiSharp.Abstractions.Messages;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;
using PiSharp.Subagents.Commands;
using Xunit;

namespace PiSharp.Subagents.Tests;

/// <summary>
/// Minimal recording IExtensionApi: captures tool/command/handler registrations, emitted events and
/// sent messages; every other surface throws so tests notice accidental use.
/// </summary>
public sealed class RecordingExtensionApi : IExtensionApi
{
    public List<ExtensionToolRegistration> RegisteredTools { get; } = [];
    public List<ExtensionCommandRegistration> RegisteredCommands { get; } = [];
    public List<(string EventName, ExtensionEventHandler Handler)> RegisteredHandlers { get; } = [];
    public List<(string EventName, object Payload)> EmittedEvents { get; } = [];
    public List<AgentMessage> SentMessages { get; } = [];

    public string Cwd { get; init; } = "/";
    public bool HasUi { get; init; }
    public IExtensionUi Ui { get; init; } = NoExtensionUi.Instance;
    public ExtensionDescriptor Descriptor { get; init; } = new("test", "Test", "0.0.0", "test");

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        RegisteredHandlers.Add((eventName, handler));
        return new RemoveOnDispose(() => RegisteredHandlers.Remove((eventName, handler)));
    }

    public IDisposable RegisterTool(ExtensionToolRegistration registration)
    {
        RegisteredTools.Add(registration);
        return new RemoveOnDispose(() => RegisteredTools.Remove(registration));
    }

    public IDisposable RegisterCommand(ExtensionCommandRegistration registration)
    {
        RegisteredCommands.Add(registration);
        return new RemoveOnDispose(() => RegisteredCommands.Remove(registration));
    }

    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn, CancellationToken cancellationToken)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public IExtensionEventBus Events => new RecordingEventBus(this);
    public IExtensionSkillApi Skills => new RecordingSkillApi();

    public IExtensionSessionApi Session => NoOpSessionApi.Instance;
    public IExtensionToolApi Tools => NoOpToolApi.Instance;
    public IExtensionModelApi Model => NoOpModelApi.Instance;
    public IExtensionPromptApi Prompt => throw new NotSupportedException();
    public IExtensionSettingsApi Settings => throw new NotSupportedException();
    public IExtensionStateApi State => throw new NotSupportedException();

    public IDisposable Use(ExtensionMiddleware middleware) => NullDisposable.Instance;
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => NullDisposable.Instance;
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => NullDisposable.Instance;
    public IDisposable RegisterFlag(ExtensionFlagRegistration registration) => NullDisposable.Instance;
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => NullDisposable.Instance;
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => NullDisposable.Instance;
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException();
    public bool RemoveProvider(string api) => throw new NotSupportedException();
    public object? GetFlag(string name) => null;
    public IReadOnlyDictionary<string, object?> GetFlags() => new Dictionary<string, object?>();

    private sealed class RecordingEventBus(RecordingExtensionApi owner) : IExtensionEventBus
    {
        public IDisposable On(string eventName, ExtensionEventHandler handler) => owner.On(eventName, handler);

        public Task EmitAsync(string eventName, object payload, CancellationToken cancellationToken = default)
        {
            owner.EmittedEvents.Add((eventName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSkillApi : IExtensionSkillApi
    {
        public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => NullDisposable.Instance;
        public Task<IReadOnlyList<ExtensionSkillDefinition>> GetAllSkillsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>([]);
        public Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["core"]);
        public Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
    private sealed class NoOpSessionApi : IExtensionSessionApi
    {
        public static readonly NoOpSessionApi Instance = new();
        public Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetNameAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpToolApi : IExtensionToolApi
    {
        public static readonly NoOpToolApi Instance = new();
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => NullDisposable.Instance;
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetActiveToolsAsync(IReadOnlyList<string> toolNames, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpModelApi : IExtensionModelApi
    {
        public static readonly NoOpModelApi Instance = new();
        public Task<bool> SetModelAsync(PiSharp.Agent.Core.Models.ModelDescriptor model, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<PiSharp.Abstractions.Options.ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default) => Task.FromResult<PiSharp.Abstractions.Options.ThinkingLevel?>(null);
        public Task SetThinkingLevelAsync(PiSharp.Abstractions.Options.ThinkingLevel level, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RemoveOnDispose(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}

public sealed class SubagentsExtensionTests : IDisposable
{
    private readonly string _tempRoot;

    public SubagentsExtensionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pisharp-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    private static string ProjectAgentsDir(string root)
    {
        var dir = Path.Combine(root, ".pi", "agents");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task InitializeRegistersTaskToolCommandsAndResourcesHandler()
    {
        var projectAgents = ProjectAgentsDir(_tempRoot);
        File.WriteAllText(Path.Combine(projectAgents, "scout.md"), """
            ---
            name: scout
            description: Read-only explorer.
            ---

            You are a scout.
            """);
        var api = new RecordingExtensionApi { Cwd = _tempRoot };
        await using var extension = new SubagentsExtension();

        await extension.InitializeAsync(api, CancellationToken.None);

        var taskRegistration = Assert.Single(api.RegisteredTools, tool => tool.Name == "task");
        Assert.False(string.IsNullOrWhiteSpace(taskRegistration.Description));
        Assert.Contains(api.RegisteredCommands, command => command.Name == AgentsCommand.Name);
        Assert.Contains(api.RegisteredCommands, command => command.Name == AgentsCommand.Alias);
        Assert.Contains(api.RegisteredHandlers, handler => handler.EventName == ExtensionEventNames.ResourcesUpdate);
    }

    [Fact]
    public async Task AgentsCommandRendersVisibleAgentsAsUserMessage()
    {
        var projectAgents = ProjectAgentsDir(_tempRoot);
        File.WriteAllText(Path.Combine(projectAgents, "scout.md"), """
            ---
            name: scout
            description: Read-only explorer.
            ---

            You are a scout.
            """);
        File.WriteAllText(Path.Combine(projectAgents, "hidden.md"), """
            ---
            name: hidden
            description: Secret agent.
            hide: true
            ---

            body
            """);
        var api = new RecordingExtensionApi { Cwd = _tempRoot };
        await using var extension = new SubagentsExtension();
        await extension.InitializeAsync(api, CancellationToken.None);

        var command = Assert.Single(api.RegisteredCommands, candidate => candidate.Name == AgentsCommand.Name);
        await command.InvokeAsync(new ExtensionCommandContext(
            AgentsCommand.Name,
            string.Empty,
            api.Ui,
            api.Session,
            api.Model,
            api.Tools,
            api.GetFlags(),
            CancellationToken.None));

        var message = Assert.Single(api.SentMessages);
        var text = Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(message).Content)).Text;
        Assert.Contains("scout", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", text, StringComparison.Ordinal);
        Assert.Contains("Read-only explorer.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResourcesUpdateRefreshesRegistry()
    {
        var projectAgents = ProjectAgentsDir(_tempRoot);
        var api = new RecordingExtensionApi { Cwd = _tempRoot };
        await using var extension = new SubagentsExtension();
        await extension.InitializeAsync(api, CancellationToken.None);

        File.WriteAllText(Path.Combine(projectAgents, "late.md"), """
            ---
            name: late
            description: Arrives after init.
            ---

            body
            """);
        var handler = Assert.Single(api.RegisteredHandlers, item => item.EventName == ExtensionEventNames.ResourcesUpdate);
        await handler.Handler(new ExtensionEvent(ExtensionEventNames.ResourcesUpdate, null!), CancellationToken.None);

        // The refreshed registry is observable through the /agents command output.
        await api.RegisteredCommands.First(command => command.Name == AgentsCommand.Name)
            .InvokeAsync(new ExtensionCommandContext(
                AgentsCommand.Name,
                string.Empty,
                api.Ui,
                api.Session,
                api.Model,
                api.Tools,
                api.GetFlags(),
                CancellationToken.None));

        var message = Assert.Single(api.SentMessages);
        Assert.Contains("late", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(message).Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeReleasesRegistrations()
    {
        var api = new RecordingExtensionApi { Cwd = _tempRoot };
        var extension = new SubagentsExtension();
        await extension.InitializeAsync(api, CancellationToken.None);

        await extension.DisposeAsync();

        Assert.Empty(api.RegisteredTools);
        Assert.Empty(api.RegisteredCommands);
        Assert.Empty(api.RegisteredHandlers);
    }
}
