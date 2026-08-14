using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;

namespace PiSharp.Extensions;

public interface IExtensionApi
{
    ExtensionDescriptor Descriptor { get; }
    string Cwd { get; }
    bool HasUi { get; }
    IExtensionUi Ui { get; }
    IExtensionSessionApi Session { get; }
    IExtensionToolApi Tools { get; }
    IExtensionSkillApi Skills { get; }
    IExtensionModelApi Model { get; }
    IExtensionEventBus Events { get; }
    IExtensionPromptApi Prompt { get; }
    IExtensionSettingsApi Settings { get; }
    IExtensionStateApi State { get; }

    IDisposable On(string eventName, ExtensionEventHandler handler);
    IDisposable Use(ExtensionMiddleware middleware);
    IDisposable RegisterTool(ExtensionToolRegistration registration);
    IDisposable RegisterSkill(ExtensionSkillRegistration registration);
    IDisposable RegisterCommand(ExtensionCommandRegistration registration);
    IDisposable RegisterShortcut(ExtensionShortcutRegistration registration);
    IDisposable RegisterFlag(ExtensionFlagRegistration registration);
    IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration);
    IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration);
    RegisteredApiProvider RegisterProvider(IModelProvider provider);
    bool RemoveProvider(string api);
    object? GetFlag(string name);
    IReadOnlyDictionary<string, object?> GetFlags();
    Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
        => SendMessageAsync(message, ExtensionMessageDelivery.NextTurn, false, cancellationToken);
    Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default);
}
