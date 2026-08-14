using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-model-roles",
    Name = "PiSharp Model Roles",
    Version = "0.1.0",
    Description = "Named model roles (@role) and effort presets from the 'modelRoles'/'effort' settings maps, registered into the global ModelRoleRegistry.")]

namespace PiSharp.ModelRoles;

/// <summary>
/// <c>pisharp-model-roles</c> extension entry. Reads the top-level
/// <c>modelRoles</c> and <c>effort</c> settings sections via
/// <see cref="IExtensionSettingsApi.GetCore"/>, serves them through a
/// <see cref="SettingsModelRoleResolver"/> registered into the global
/// <see cref="ModelRoleRegistry"/>, rebuilds the map when a
/// <c>settings_changed</c> event touches either section, and registers the
/// <c>/roles</c> (alias <c>/model-roles</c>) slash command. Unregisters its
/// resolver and subscriptions on dispose.
/// </summary>
public sealed class ModelRolesExtension : IExtension, IAsyncDisposable
{
    private const string ModelRolesKey = "modelRoles";
    private const string EffortKey = "effort";

    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<string> _diagnostics = [];
    private IExtensionApi? _api;
    private SettingsModelRoleResolver? _resolver;
    private bool _disposed;

    /// <summary>The resolver currently registered in <see cref="ModelRoleRegistry"/>, or null.</summary>
    internal SettingsModelRoleResolver? Resolver
    {
        get { lock (_gate) return _resolver; }
    }

    /// <summary>Parse diagnostics (warnings for skipped/invalid entries) from the most recent rebuild.</summary>
    internal IReadOnlyList<string> Diagnostics
    {
        get { lock (_gate) return _diagnostics.ToArray(); }
    }

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;

        // Initial parse/registration before any change notifications can arrive.
        Rebuild();

        _subscriptions.Add(api.On(ExtensionEventNames.SettingsChanged, OnSettingsChangedAsync));
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration(
            "roles",
            "List all configured model roles, their candidate selectors, and effort presets. Alias: /model-roles",
            OnRolesCommandAsync)));
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration(
            "model-roles",
            "List all configured model roles, their candidate selectors, and effort presets.",
            OnRolesCommandAsync)));

        return Task.CompletedTask;
    }

    private Task OnSettingsChangedAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (evt.Payload is ExtensionSettingsChange change && IsRelevantKey(change.Key))
        {
            Rebuild();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the change targets the <c>modelRoles</c> or <c>effort</c> core section:
    /// the section itself or any nested key beneath it.
    /// </summary>
    private static bool IsRelevantKey(string key)
        => key.Equals(ModelRolesKey, StringComparison.Ordinal)
        || key.Equals(EffortKey, StringComparison.Ordinal)
        || key.StartsWith(ModelRolesKey + ".", StringComparison.Ordinal)
        || key.StartsWith(EffortKey + ".", StringComparison.Ordinal);

    /// <summary>
    /// Re-parses both settings sections and swaps the registered resolver,
    /// unregistering the previous one so removed roles stop resolving.
    /// </summary>
    private void Rebuild()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var api = _api;
            if (api is null) return;

            _diagnostics.Clear();
            var roles = ModelRolesSettingsParser.Parse(
                ToJsonElement(api.Settings.GetCore(ModelRolesKey)),
                ToJsonElement(api.Settings.GetCore(EffortKey)),
                _diagnostics.Add);

            var resolver = new SettingsModelRoleResolver(api.Descriptor.EffectiveSourceId, roles);

            if (_resolver is not null)
            {
                ModelRoleRegistry.Unregister(_resolver.SourceId);
            }
            ModelRoleRegistry.Register(resolver);
            _resolver = resolver;
        }
    }

    private Task OnRolesCommandAsync(string args, CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null) return Task.CompletedTask;

        SettingsModelRoleResolver? resolver;
        lock (_gate) resolver = _resolver;

        var lines = new List<string>();
        if (resolver is null || resolver.Roles.Count == 0)
        {
            lines.Add("No model roles configured. Define a 'modelRoles' settings section (and optional 'effort' presets) to create roles.");
        }
        else
        {
            lines.Add("Known model roles:");
            foreach (var role in resolver.Roles)
            {
                var resolution = resolver.Resolve(role);
                if (resolution is null) continue;
                var effortSuffix = resolution.Effort is null ? string.Empty : $" (effort: {FormatEffort(resolution.Effort)})";
                lines.Add($"@{role} -> {string.Join(", ", resolution.Selectors)}{effortSuffix}");
            }
        }

        return api.SendMessageAsync(AgentMessages.User(string.Join("\n", lines)), cancellationToken);
    }

    private static string FormatEffort(EffortPreset effort)
    {
        var parts = new List<string>();
        if (effort.ThinkingLevel is { } level)
        {
            parts.Add($"thinking={level.ToString().ToLowerInvariant()}");
        }
        if (effort.Budgets is { Count: > 0 } budgets)
        {
            parts.Add("budgets={" + string.Join(", ", budgets.Select(b => $"{b.Key}={b.Value}")) + "}");
        }
        return parts.Count == 0 ? "no change" : string.Join(", ", parts);
    }

    private static JsonElement? ToJsonElement(object? value) => value switch
    {
        null => null,
        JsonElement element => element,
        JsonNode node => JsonSerializer.SerializeToElement(node),
        _ => JsonSerializer.SerializeToElement(value)
    };

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            if (_resolver is not null)
            {
                ModelRoleRegistry.Unregister(_resolver.SourceId);
            }
            _resolver = null;
            _api = null;
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            _diagnostics.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
