using PiSharp.Abstractions.Environment;
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
    IDisposable RegisterSkill(ExtensionSkillDefinition registration);
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

    /// <summary>
    /// Publishes a named extension-originated session event to harness
    /// subscribers and the daemon wire via the C3 CustomEvent lane. The name
    /// must match <c>[a-z0-9_]{1,64}</c> and not collide with a core session
    /// event name; the payload must be JSON-serializable. Defaults to a no-op
    /// when the host does not wire it.
    /// </summary>
    Task EmitClientEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Returns all registered slash commands (extension, prompt, skill) with
    /// their source metadata. Mirrors JS pi <c>pi.getCommands()</c>. Defaults
    /// to an empty list when the host does not wire it.
    /// </summary>
    Task<IReadOnlyList<ExtensionCommandInfo>> GetCommandsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ExtensionCommandInfo>>([]);

    /// <summary>
    /// Model-completion surface (the counterpart to the <see cref="Model"/>
    /// set-model surface). Defaults to a throwing no-op so existing
    /// implementors and in-memory fakes are not forced to implement it.
    /// </summary>
    IExtensionCompletionApi Completion
        => throw new NotSupportedException("This extension host does not provide a completion API.");

    /// <summary>
    /// Internal URL resolver registration surface. Defaults to a throwing
    /// no-op when the host does not provide a registry.
    /// </summary>
    IExtensionUrlApi Urls
        => throw new NotSupportedException("This extension host does not provide an internal URL registry.");

    /// <summary>
    /// Telemetry emit surface (span/event/metrics). Defaults to a throwing
    /// no-op so existing implementors and in-memory fakes are not forced to
    /// implement it; hosts that wire the runtime <c>TelemetryService</c> return
    /// a live facade here.
    /// </summary>
    IExtensionTelemetryApi Telemetry
        => throw new NotSupportedException("This extension host does not provide a telemetry API.");

    /// <summary>
    /// File-content extractor registration surface. Defaults to a throwing
    /// no-op when the host does not provide a registry.
    /// </summary>
    IExtensionFileApi Files
        => throw new NotSupportedException("This extension host does not provide a file-content extractor registry.");

    /// <summary>
    /// Search provider registration surface. Defaults to a throwing no-op when
    /// the host does not provide a registry.
    /// </summary>
    IExtensionSearchApi Search
        => throw new NotSupportedException("This extension host does not provide a search provider registry.");

    /// <summary>
    /// Rule-provider registration + query surface. Defaults to a throwing
    /// no-op when the host does not wire a rules registry.
    /// </summary>
    IExtensionRuleApi Rules
        => throw new NotSupportedException("This extension host does not provide a rules API.");

    /// <summary>
    /// Stream-delta interception registration surface (P10 TTSR). Null when the host
    /// does not wire a registry; extensions fall back to their own registration seam.
    /// </summary>
    IStreamDeltaInterceptorApi? StreamDelta => null;

    /// <summary>
    /// Runtime package management surface (install/update/remove/list
    /// installed extension packages, GAP-55). Defaults to a throwing no-op so
    /// existing implementors and in-memory fakes are not forced to implement
    /// it; hosts that wire the runtime package service return a live facade
    /// here.
    /// </summary>
    IExtensionPackageApi Packages
        => throw new NotSupportedException("This extension host does not provide a package API.");

    /// <summary>
    /// The execution environment (file system + shell) the host runs the
    /// session under, or null when the host does not expose one.
    /// </summary>
    IExecutionEnv? ExecutionEnv => null;
}
