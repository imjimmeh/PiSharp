using System.Text.Json;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// Verifies the extension-surface default interface members added by the
/// session-control / advisor / declarative-tools / internal-urls / eval / mcp
/// spine: hosts that do not wire a capability must fall back to the default
/// (empty / cancelled / throwing) behavior instead of breaking implementors.
/// </summary>
public sealed class ExtensionDefaultApiTests
{
    private sealed class MinimalSessionApi : IExtensionSessionApi
    {
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<string?> GetNameAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SetNameAsync(string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MinimalToolApi : IExtensionToolApi
    {
        public IDisposable RegisterTool(ExtensionToolRegistration registration)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SetActiveToolsAsync(IReadOnlyList<string> toolNames, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MinimalApi : IExtensionApi
    {
        public ExtensionDescriptor Descriptor => new("test", "test", "1.0");
        public string Cwd => string.Empty;
        public bool HasUi => false;
        public IExtensionUi Ui => NoExtensionUi.Instance;
        public IExtensionSessionApi Session => new MinimalSessionApi();
        public IExtensionToolApi Tools => new MinimalToolApi();
        public IExtensionSkillApi Skills => throw new NotSupportedException();
        public IExtensionModelApi Model => throw new NotSupportedException();
        public IExtensionEventBus Events => throw new NotSupportedException();
        public IExtensionPromptApi Prompt => throw new NotSupportedException();
        public IExtensionSettingsApi Settings => throw new NotSupportedException();
        public IExtensionStateApi State => throw new NotSupportedException();
        public IDisposable On(string eventName, ExtensionEventHandler handler) => throw new NotSupportedException();
        public IDisposable Use(ExtensionMiddleware middleware) => throw new NotSupportedException();
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => throw new NotSupportedException();
        public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => throw new NotSupportedException();
        public IDisposable RegisterCommand(ExtensionCommandRegistration registration) => throw new NotSupportedException();
        public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => throw new NotSupportedException();
        public IDisposable RegisterFlag(ExtensionFlagRegistration registration) => throw new NotSupportedException();
        public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => throw new NotSupportedException();
        public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => throw new NotSupportedException();
        public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException();
        public bool RemoveProvider(string api) => throw new NotSupportedException();
        public object? GetFlag(string name) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, object?> GetFlags() => throw new NotSupportedException();
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task GetCommandsAsync_DefaultsToEmptyList()
    {
        IExtensionApi api = new MinimalApi();
        var commands = await api.GetCommandsAsync();
        Assert.Empty(commands);
    }

    [Fact]
    public void Completion_DefaultsToThrowing()
    {
        IExtensionApi api = new MinimalApi();
        Assert.Throws<NotSupportedException>(() => api.Completion);
    }

    [Fact]
    public void Urls_DefaultsToThrowing()
    {
        IExtensionApi api = new MinimalApi();
        Assert.Throws<NotSupportedException>(() => api.Urls);
    }

    [Fact]
    public void Telemetry_DefaultsToThrowing()
    {
        IExtensionApi api = new MinimalApi();
        Assert.Throws<NotSupportedException>(() => api.Telemetry);
    }

    [Fact]
    public void ExecutionEnv_DefaultsToNull()
    {
        IExtensionApi api = new MinimalApi();
        Assert.Null(api.ExecutionEnv);
    }

    [Fact]
    public async Task SessionDefaults_NewSessionIsCancelled()
    {
        IExtensionSessionApi session = new MinimalSessionApi();
        var result = await session.NewSessionAsync();
        Assert.True(result.Cancelled);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task SessionDefaults_ForkIsCancelled()
    {
        IExtensionSessionApi session = new MinimalSessionApi();
        var result = await session.ForkAsync();
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task SessionDefaults_SwitchIsCancelled()
    {
        IExtensionSessionApi session = new MinimalSessionApi();
        var result = await session.SwitchSessionAsync("some-session");
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task SessionDefaults_NavigateAndIdleAreNoOps()
    {
        IExtensionSessionApi session = new MinimalSessionApi();
        await session.NavigateTreeAsync("target");
        await session.WaitForIdleAsync();
        Assert.True(await session.IsIdleAsync());
        Assert.False(await session.HasPendingMessagesAsync());
    }

    [Fact]
    public async Task SessionDefaults_WithSessionCallbackIsNotInvokedWhenCancelled()
    {
        IExtensionSessionApi session = new MinimalSessionApi();
        var invoked = false;
        var result = await session.NewSessionAsync((_, _) => { invoked = true; return Task.CompletedTask; });
        Assert.True(result.Cancelled);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ToolDefaults_ExecuteToolReturnsErrorText()
    {
        IExtensionToolApi tools = new MinimalToolApi();
        var result = await tools.ExecuteToolAsync("missing-tool", JsonDocument.Parse("{}").RootElement);
        Assert.NotNull(result);
        Assert.Contains("not available", result.Content.OfType<TextContent>().Single().Text);
    }
}
