using PiSharp.Extensions;
using PiSharp.Subagents.Commands;
using PiSharp.Subagents.Discovery;
using PiSharp.Subagents.Spawning;
using PiSharp.Subagents.Tools;

[assembly: ExtensionMetadata("subagents", Name = "PiSharp Subagents", Version = "0.1.0", SourceId = "pi:extension:subagents")]

namespace PiSharp.Subagents;

/// <summary>
/// Entry point of the subagent framework plugin: discovers agent definitions (project → user →
/// extension → bundled first-wins), registers the <c>task</c> spawn tool and the <c>/agents</c>
/// command, and wires the spawn coordinator. Child <c>yield</c> injection and guardrail enforcement
/// live in the runtime service (C1/C2).
/// </summary>
public sealed class SubagentsExtension : IExtension, IAsyncDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private readonly AgentDefinitionRegistry _registry = new();
    private CancellationTokenSource _lifetimeCts = new();
    private AgentDefinitionDiscovery? _discovery;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);

        // Settings: read from environment until the IExtensionApi.Settings member lands (P02).
        // TODO(settings): replace with PiSettingsStore `subagents.*` reads once available — keys:
        // agentsDir, disabledAgents, maxRecursionDepth, autoloadSkills, defaultTools, isolationBackend.
        var settings = SubagentSettings.FromEnvironment();
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectDir = Path.Combine(api.Cwd, ".pi", "agents");
        var userDir = string.IsNullOrWhiteSpace(homeDirectory)
            ? null
            : Path.Combine(homeDirectory, ".pi", "agent", "agents");
        var extensionDirs = settings.AgentsDir
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(Path.GetFullPath(root), "agents"))
            .ToArray();
        _discovery = new AgentDefinitionDiscovery(
            projectDirs: [projectDir],
            userDirs: userDir is null ? [] : [userDir],
            extensionDirs: extensionDirs);

        RefreshRegistry(settings);

        // The parent agent name is unknown at extension init; the self-recursion guard takes effect
        // one level down, where child coordinators carry the spawning agent's name.
        var coordinator = new SubagentSpawnCoordinator(
            _registry,
            settings,
            service: null, // resolved lazily via SubagentRuntimeAccess.Current per spawn
            getParentSelectedSkills: (ct) => SafeGetSelectedSkillsAsync(api, ct),
            emitEvent: (name, payload, ct) => SafeEmitAsync(api, name, payload, ct),
            parentSessionId: null,
            parentAgentName: null,
            depth: 0,
            parentSpawns: []);

        _registrations.Add(api.RegisterTool(new TaskTool(coordinator).ToRegistration()));
        _registrations.Add(api.RegisterCommand(AgentsCommand.Create(_registry, api)));
        _registrations.Add(api.RegisterCommand(AgentsCommand.CreateAlias(_registry, api)));
        _registrations.Add(api.On(ExtensionEventNames.ResourcesUpdate, (_, _) =>
        {
            RefreshRegistry(settings);
            return Task.CompletedTask;
        }));
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();
        foreach (var registration in _registrations)
        {
            try { registration.Dispose(); }
            catch { }
        }
        _registrations.Clear();
        await Task.CompletedTask;
    }

    private void RefreshRegistry(SubagentSettings settings)
    {
        if (_discovery is null)
            return;
        var definitions = _discovery.Discover();
        _registry.Replace(definitions, settings.DisabledAgents);
    }

    private static async Task<IReadOnlyList<string>> SafeGetSelectedSkillsAsync(IExtensionApi api, CancellationToken cancellationToken)
    {
        try { return await api.Skills.GetSelectedSkillsAsync(cancellationToken); }
        catch { return []; }
    }

    private static Task SafeEmitAsync(IExtensionApi api, string eventName, object? payload, CancellationToken cancellationToken)
    {
        try { return api.Events.EmitAsync(eventName, payload ?? new { }, cancellationToken); }
        catch { return Task.CompletedTask; }
    }
}
