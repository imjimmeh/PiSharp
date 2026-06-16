using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.Extensions.Testing;

public sealed class FakeExtensionApi : IExtensionApi
{
    private readonly List<ExtensionToolRegistration> _tools = [];
    private readonly List<(string EventName, ExtensionEventHandler Handler)> _handlers = [];
    private readonly List<ExtensionMiddleware> _middlewares = [];
    private readonly List<CapturedMessage> _sentMessages = [];

    public string Cwd { get; set; } = "/";
    public bool HasUi { get; set; }
    public IExtensionUi Ui { get; set; } = NoExtensionUi.Instance;
    public ExtensionDescriptor Descriptor { get; set; } = FakeExtensionDescriptor.Default;

    public IReadOnlyList<ExtensionToolRegistration> RegisteredTools => _tools;
    public IReadOnlyList<(string EventName, ExtensionEventHandler Handler)> RegisteredHandlers => _handlers;
    public IReadOnlyList<ExtensionMiddleware> RegisteredMiddlewares => _middlewares;
    public IReadOnlyList<CapturedMessage> SentMessages => _sentMessages;

    // Captured registrations
    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        _handlers.Add((eventName, handler));
        return NullDisposable.Instance;
    }

    public IDisposable Use(ExtensionMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return NullDisposable.Instance;
    }

    public IDisposable RegisterTool(ExtensionToolRegistration registration)
    {
        _tools.Add(registration);
        return NullDisposable.Instance;
    }

    public Task SendMessageAsync(
        AgentMessage message,
        ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp,
        bool triggerTurn = false,
        CancellationToken cancellationToken = default)
    {
        _sentMessages.Add(new CapturedMessage(message, delivery, triggerTurn, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    // Sub-APIs — not supported; use the captured lists above
    public IExtensionSessionApi Session =>
        throw new NotSupportedException("FakeExtensionApi.Session is not supported. Use SentMessages to inspect sent messages.");
    public IExtensionToolApi Tools =>
        throw new NotSupportedException("FakeExtensionApi.Tools is not supported. Use RegisteredTools to inspect tool registrations.");
    public IExtensionSkillApi Skills =>
        throw new NotSupportedException("FakeExtensionApi.Skills is not supported.");
    public IExtensionModelApi Model =>
        throw new NotSupportedException("FakeExtensionApi.Model is not supported.");
    public IExtensionEventBus Events =>
        throw new NotSupportedException("FakeExtensionApi.Events is not supported. Use RegisteredHandlers to inspect event registrations.");
    public IExtensionPromptApi Prompt =>
        throw new NotSupportedException("FakeExtensionApi.Prompt is not supported.");

    // Remaining IExtensionApi members — throw until promoted if a test needs them
    public IDisposable RegisterSkill(ExtensionSkillRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterSkill is not supported.");
    public IDisposable RegisterCommand(ExtensionCommandRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterCommand is not supported.");
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterShortcut is not supported.");
    public IDisposable RegisterFlag(ExtensionFlagRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterFlag is not supported.");
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterMessageRenderer is not supported.");
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterMessageDecorator is not supported.");
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterProvider is not supported.");
    public bool RemoveProvider(string api) =>
        throw new NotSupportedException("FakeExtensionApi.RemoveProvider is not supported.");
    public object? GetFlag(string name) => null;
    public IReadOnlyDictionary<string, object?> GetFlags() => new Dictionary<string, object?>();

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
