using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Compatibility.Settings;
using PiSharp.ContinualHarness.Contracts;
using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-refine", Name = "Continual Harness", Version = "1.0.0")]

namespace PiSharp.ContinualHarness;

/// <summary>
/// <c>pisharp-refine</c> extension entry point. Registers the <c>/refine</c> slash command, the
/// optional <c>refine</c> model tool, and the prompt contributor; wires the per-scope journals and
/// the write-target adapters. Reads <c>extensions.pisharp-refine.*</c> settings; a disabled master
/// gate (or the <c>refine</c> flag) registers nothing / no-ops.
/// </summary>
public sealed class ContinualHarnessExtension : IExtension, IAsyncDisposable
{
    private readonly IHarnessMemoryStore? _memoryStore;
    private readonly List<IDisposable> _registrations = [];
    private ILogger _logger = NullLogger<ContinualHarnessExtension>.Instance;
    private IExtensionApi? _api;
    private HarnessRefinementService? _service;
    private IHarnessSettings? _settings;
    private Func<bool>? _gate;


    public ContinualHarnessExtension() { }

    /// <summary>Injection point for a P08 memory-store adapter once one is wireable from a plugin ALC.</summary>
    public ContinualHarnessExtension(IHarnessMemoryStore memoryStore) => _memoryStore = memoryStore;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _settings = new HarnessSettings(api.Settings);
        _gate = () => api.GetFlag("refine") is not false;

        if (!_settings.Enabled)
        {
            _logger.LogInformation("pisharp-refine is disabled by settings; not registering.");

            return Task.CompletedTask;
        }

        var paths = PiAgentPaths.FromCwd(api.Cwd);

        var local = new HarnessStore(HarnessRefinementScope.Local, JournalPath(paths.ProjectPiSharpDirectory)).Load(cancellationToken);
        var global = new HarnessStore(HarnessRefinementScope.Global, JournalPath(paths.GlobalPiSharpDirectory)).Load(cancellationToken);

        var projectAgents = Path.Combine(paths.ProjectPiDirectory, "agents");
        var globalAgents = Path.Combine(paths.GlobalAgentDirectory, "agents");

        var emitEvent = (string name, object? payload, CancellationToken ct) => api.Events.EmitAsync(name, payload!, ct);
        var appendAudit = (string customType, object? payload, CancellationToken ct) => api.Session.AppendEntryAsync(customType, payload!, ct);

        _service = new HarnessRefinementService(
            local,
            global,
            targetFactory: (kind, scope) => ResolveTarget(kind, scope, api, projectAgents, globalAgents),
            settings: _settings,
            emitEvent: emitEvent,
            appendAudit: appendAudit,
            ui: api.Ui);

        _registrations.Add(api.RegisterFlag(new ExtensionFlagRegistration(
            "refine",
            "Continual-harness continuation gate (false disables /refine and the refine tool).",
            ExtensionFlagType.Boolean,
            true)));

        var slashCommand = new RefineSlashCommand(api, _service, _settings, _gate, sessionId: "user");
        _registrations.Add(api.RegisterCommand(new ExtensionCommandRegistration(
            "refine",
            "Create/update/delete/rollback supplemental harness state (prompt|memory|skill|subagent).",
            (args, ct) => slashCommand.InvokeAsync(args, ct))));

        if (_settings.ToolEnabled)
        {
            var tool = new RefineTool(_service, _settings, _gate);
            _registrations.Add(api.RegisterTool(new ExtensionToolRegistration(
                RefineTool.ToolName,
                "Refine harness state",
                "Creates/updates/deletes supplemental harness state (prompt, memory, skill, subagent). Evidence-cited, versioned, rollback-able.",
                RefineTool.BuildSchema(),
                (id, parameters, ct, onUpdate) => tool.ExecuteAsync(id, parameters, ct, onUpdate))));
        }

        _registrations.Add(api.Prompt.RegisterContributor(new HarnessPromptContributor(GetPromptEntries)));


        _logger.LogInformation("pisharp-refine initialized (enabled).");
        return Task.CompletedTask;
    }

    private IRefinementTarget ResolveTarget(
        HarnessRefinementKind kind,
        HarnessRefinementScope scope,
        IExtensionApi api,
        string projectAgents,
        string globalAgents)
    {
        switch (kind)
        {
            case HarnessRefinementKind.Prompt:
                return new PromptSectionTarget();
            case HarnessRefinementKind.Subagent:
                return new AgentDefinitionTarget(scope == HarnessRefinementScope.Local ? projectAgents : globalAgents);
            case HarnessRefinementKind.Skill:
            {
                IExtensionManagedSkillApi? managed;
                try { managed = api.Skills.ManagedSkills; }
                catch (NotSupportedException) { managed = null; }
                if (managed is null)
                    throw new HarnessRejectedException("Skill-kind refinements are unavailable: the host does not provide the P04 managed-skill store.");
                return new ManagedSkillTarget(managed);
            }
            case HarnessRefinementKind.Memory:
                if (_memoryStore is not null)
                    return new MemoryTarget(_memoryStore);
                var p08 = new P08MemoryStoreAdapter(
                    scope == HarnessRefinementScope.Local ? PiSharp.Memory.Abstractions.MemoryScope.Project : PiSharp.Memory.Abstractions.MemoryScope.User);
                if (!p08.IsAvailable)
                    throw new HarnessRejectedException("Memory-kind refinements are unavailable: the P08 memory store has no backend.");
                return new MemoryTarget(p08);
            default:
                throw new HarnessRejectedException($"Unsupported refinement kind '{kind}'.");
        }
    }

    private IEnumerable<HarnessEntry> GetPromptEntries()
    {
        if (_service is null) yield break;
        var merged = new Dictionary<HarnessEntryKey, HarnessEntry>();
        if (_service.Global is not null)
        {
            foreach (var pair in _service.Global.Effective) merged[pair.Key] = pair.Value;
        }
        foreach (var pair in _service.Local.Effective) merged[pair.Key] = pair.Value;
        foreach (var entry in merged.Values.Where(e => e.Key.Kind == HarnessRefinementKind.Prompt))
            yield return entry;
    }

    private static string JournalPath(string piSharpRoot)
        => Path.Combine(piSharpRoot, "harness", "refinements.jsonl");

    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        await ValueTask.CompletedTask;
    }
}
