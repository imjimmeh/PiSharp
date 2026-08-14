using System.Text.Json;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Abstractions.Messages;
using PiSharp.ContinualHarness;
using PiSharp.ContinualHarness.Contracts;
using PiSharp.Extensions;

namespace PiSharp.ContinualHarness.Tests;

/// <summary>Bare-bones <see cref="IHarnessSettings"/> with mutable defaults.</summary>
public sealed class HarnessSettingsStub : IHarnessSettings
{
    public bool Enabled { get; set; } = true;
    public bool ToolEnabled { get; set; } = true;
    public bool RequireEvidence { get; set; } = true;
    public string ConflictPolicy { get; set; } = "ask";
    public string DefaultScope { get; set; } = "local";
    public IReadOnlyList<string> AllowedKinds { get; set; } = ["prompt", "memory", "skill", "subagent"];
    public int MaxContentBytes { get; set; } = 65536;
}

/// <summary>Captures <c>NotifyAsync</c> output and drives interactive flows from a canned queue.</summary>
public sealed class StubUi : IExtensionUi
{
    public List<(string Message, ExtensionUiSeverity Severity)> Notifications { get; } = [];
    public readonly Queue<string?> Promises = new();
    public List<(string extensionId, string? status)> Statuses { get; } = [];
    public List<(string extensionId, ExtensionWidgetState? widget)> Widgets { get; } = [];

    public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default)
    {
        Notifications.Add((message, severity));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(Promises.Count > 0 ? Promises.Dequeue() == "true" : true);

    public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Promises.Count > 0 ? Promises.Dequeue() : initialValue);

    public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        => Task.FromResult(Promises.Count > 0 ? Promises.Dequeue() : options.FirstOrDefault());

    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
    {
        Statuses.Add((extensionId, status));
        return Task.CompletedTask;
    }

    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
    {
        Widgets.Add((extensionId, widget));
        return Task.CompletedTask;
    }
}

/// <summary>Captures session audit entries.</summary>
public sealed class StubSessionApi : IExtensionSessionApi
{
    public List<(string CustomType, object? Data)> Audits { get; } = [];

    public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default)
    {
        Audits.Add((customType, data));
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task SetNameAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Captures in-process event emissions.</summary>
public sealed class StubEventBus : IExtensionEventBus
{
    public List<(string Name, object? Payload)> Emitted { get; } = [];

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        handlers.Add((eventName, handler));
        return new NoopDisposable();
    }

    public async Task EmitAsync(string eventName, object payload, CancellationToken cancellationToken = default)
    {
        Emitted.Add((eventName, payload));
        foreach (var (name, handler) in handlers.Where(h => h.Item1 == eventName).ToList())
            await handler(new ExtensionEvent(eventName, null!, payload), cancellationToken);
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    private readonly List<(string, ExtensionEventHandler)> handlers = [];
}

/// <summary>Captures prompt contributors.</summary>
public sealed class StubPromptApi : IExtensionPromptApi
{
    public List<IPromptContributor> Contributors { get; } = [];

    public IDisposable RegisterContributor(IPromptContributor contributor)
    {
        Contributors.Add(contributor);
        return new NoopDisposable();
    }

    public IDisposable RegisterSection(PromptSection section) => new NoopDisposable();
    public IDisposable RegisterSection(ExtensionPromptSectionRegistration registration) => new NoopDisposable();
    public IDisposable RegisterTransform(IPromptTransform transform) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}

/// <summary>In-memory settings API for the top-level extension gate test.</summary>
public sealed class StubSettingsApi : IExtensionSettingsApi
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    public object? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public T? Get<T>(string key)
    {
        var value = Get(key);
        if (value is null) return default;
        if (value is T t) return t;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    }

    public object? GetCore(string path) => Get(path);

    public Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        if (value is null) Values.Remove(key); else Values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }
    public IDisposable OnChange(Action<ExtensionSettingsChange> handler) => new NoopDisposable();
    public IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler) => new NoopDisposable();
    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}

/// <summary>
/// Minimal <see cref="IExtensionApi"/> for registration/routing tests. Sub-APIs that the plugin does
/// not touch throw; command/flag/tool registration and the audit/session/event/prompt surfaces are
/// captured.
/// </summary>
public sealed class StubApi : IExtensionApi
{
    public string Cwd { get; set; } = "C:\\proj";
    public bool HasUi { get; set; } = true;
    public IExtensionUi Ui { get; set; } = new StubUi();
    public IExtensionSessionApi Session { get; set; } = new StubSessionApi();
    public IExtensionEventBus Events { get; set; } = new StubEventBus();
    public IExtensionPromptApi Prompt { get; set; } = new StubPromptApi();
    public IExtensionSettingsApi Settings { get; set; } = new StubSettingsApi();
    public IExtensionStateApi State => Throw<IExtensionStateApi>();


    public List<ExtensionCommandRegistration> Commands { get; } = [];
    public List<ExtensionFlagRegistration> Flags { get; } = [];
    public List<ExtensionToolRegistration> RegisteredTools { get; } = [];
    public Dictionary<string, object?> FlagValues { get; } = new();
    public ExtensionDescriptor Descriptor { get; set; } = new("test", "test", "1.0.0");

    public IDisposable RegisterCommand(ExtensionCommandRegistration registration)
    {
        Commands.Add(registration);
        return new Noop();
    }
    public IDisposable RegisterFlag(ExtensionFlagRegistration registration)
    {
        Flags.Add(registration);
        FlagValues[registration.Name] = registration.DefaultValue;
        return new Noop();
    }
    public IDisposable RegisterTool(ExtensionToolRegistration registration)
    {
        RegisteredTools.Add(registration);
        return new Noop();
    }
    public object? GetFlag(string name) => FlagValues.TryGetValue(name, out var value) ? value : null;
    public IReadOnlyDictionary<string, object?> GetFlags() => FlagValues;

    public IExtensionToolApi Tools => Throw<IExtensionToolApi>();
    public IExtensionSkillApi Skills => Throw<IExtensionSkillApi>();
    public IExtensionModelApi Model => Throw<IExtensionModelApi>();
    public IDisposable On(string eventName, ExtensionEventHandler handler) => new Noop();
    public IDisposable Use(ExtensionMiddleware middleware) => new Noop();
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => new Noop();
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => new Noop();
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => new Noop();
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => new Noop();
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException();
    public bool RemoveProvider(string api) => throw new NotSupportedException();
    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static T Throw<T>() where T : class => throw new NotSupportedException($"StubApi does not provide {typeof(T).Name}.");
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

/// <summary>In-memory fake managed-skill API (P04 contract).</summary>
public sealed class FakeManagedSkillApi : IExtensionManagedSkillApi
{
    public Dictionary<string, ManagedSkillDescriptor> Skills { get; } = new(StringComparer.Ordinal);

    public Task<ManagedSkillDescriptor> CreateAsync(ManagedSkillCreateRequest request, CancellationToken ct = default)
    {
        if (Skills.ContainsKey(request.Name)) throw new InvalidOperationException($"Skill '{request.Name}' already exists.");
        var descriptor = new ManagedSkillDescriptor(request.Name, request.Description, request.Content, request.DisableModelInvocation, "managed", 1);
        Skills[request.Name] = descriptor;
        return Task.FromResult(descriptor);
    }

    public Task<ManagedSkillDescriptor> UpdateAsync(string name, ManagedSkillUpdateRequest request, CancellationToken ct = default)
    {
        var existing = Skills[name];
        var updated = existing with
        {
            Description = request.Description ?? existing.Description,
            Content = request.Content ?? existing.Content,
            DisableModelInvocation = request.DisableModelInvocation ?? existing.DisableModelInvocation,
        };
        Skills[name] = updated;
        return Task.FromResult(updated);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Skills.Remove(name));

    public Task<IReadOnlyList<ManagedSkillDescriptor>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ManagedSkillDescriptor>>(Skills.Values.ToList());

    public Task<ManagedSkillDescriptor> PromoteAsync(string sourceReference, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>In-memory fake P08 memory store backing the plugin's seam.</summary>
public sealed class FakeMemoryStore : IHarnessMemoryStore
{
    public Dictionary<string, JsonElement> Records { get; } = new(StringComparer.Ordinal);
    public string Describe => "api:Memory(fake)";

    public Task<JsonElement?> GetAsync(string recordKey, CancellationToken ct = default)
        => Task.FromResult(Records.TryGetValue(recordKey, out var value) ? value.Clone() : (JsonElement?)null);

    public Task PutAsync(string recordKey, JsonElement content, CancellationToken ct = default)
    {
        Records[recordKey] = content.Clone();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string recordKey, CancellationToken ct = default)
    {
        Records.Remove(recordKey);
        return Task.CompletedTask;
    }
}

/// <summary>Content-payload builders.</summary>
public static class HarnessTestJson
{
    public static JsonElement Prompt(string markdown, string slot = "instructions", int priority = 0)
        => JsonSerializer.SerializeToElement(new { markdown, slot, priority });

    public static JsonElement Subagent(string markdown)
        => JsonSerializer.SerializeToElement(new { markdown });

    public static JsonElement Skill(string description, string content)
        => JsonSerializer.SerializeToElement(new { description, content, disableModelInvocation = false });

    public static JsonElement Memory(string title, string content, string kind = "fact")
        => JsonSerializer.SerializeToElement(new { recordKey = "refine/x", kind, title, content, tags = Array.Empty<string>() });
}
