using PiSharp.Extensions;
using PiSharp.Memory.Abstractions;
using PiSharp.Tools;

[assembly: ExtensionMetadata(
    "pisharp-memory",
    Name = "PiSharp Memory",
    Version = "1.0.0",
    Description = "Model-facing memory system: retain/recall/reflect/memory_edit/learn tools, per-project mental-model prompt injection, /memory command, auto-learn capture (off by default). Backends are separate plugins registered into MemoryServices.Providers; gated by extensions.pisharp-memory.backend.")]

namespace PiSharp.Memory;

/// <summary>
/// <c>pisharp-memory</c> core-plugin entry. Registers the five memory tools, the
/// <c>/memory</c> command, the mental-model prompt contributor and the auto-learn
/// hooks, and resolves the active backend from <see cref="MemoryServices.Providers"/>
/// by <c>extensions.pisharp-memory.backend</c>. When <c>enabled</c> is false nothing
/// is registered (zero footprint); unknown backends fall back to "off".
/// </summary>
public sealed class MemoryExtension : IExtension, IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();

    private IExtensionApi? _api;
    private MemoryStore? _store;
    private MemoryToolCoordinator? _coordinator;
    private MemoryCommandHandler? _commandHandler;
    private AutolearnService? _autolearn;

    private IMemoryProvider? _fallbackOff;
    private MemorySettings _settings = MemorySettings.Default;
    private IReadOnlyList<MemoryRecord>? _mentalModelCache;
    private bool _mentalModelsInjected;
    private bool _disposed;

    public MemoryStore? Store
    {
        get { lock (_gate) return _store; }
    }

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _settings = MemorySettings.Read(api.Settings);

        if (!_settings.Enabled) return;

        _fallbackOff = new DisabledMemoryProvider();
        var provider = ResolveBackend(_settings.Backend);
        var store = new MemoryStore(provider, MemoryProjectKeys.Encode(api.Cwd));
        lock (_gate) _store = store;
        MemoryServices.Store = store;

        var coordinator = new MemoryToolCoordinator(store, sessionContextProvider: null);
        _coordinator = coordinator;
        _commandHandler = new MemoryCommandHandler(api, store, () => _settings);

        RegisterTools(api);

        var promptContributor = new MentalModelPromptContributor(
            () => _mentalModelCache,
            () => _mentalModelsInjected,
            () => { lock (_gate) _mentalModelsInjected = true; });
        _subscriptions.Add(api.Prompt.RegisterContributor(promptContributor));

        _autolearn = new AutolearnService(
            api,
            enabled: () => _settings.AutolearnEnabled,
            autoContinue: () => _settings.AutolearnAutoContinue,
            minToolCalls: () => _settings.AutolearnMinToolCalls);
        _subscriptions.Add(api.On(ExtensionEventNames.AgentEnd, _autolearn.OnAgentEndAsync));
        _subscriptions.Add(api.On(ExtensionEventNames.Settled, _autolearn.OnSettledAsync));
        _subscriptions.Add(api.Settings.OnChange(change => _ = OnSettingsChangedAsync(change, CancellationToken.None)));

        _subscriptions.Add(api.On(ExtensionEventNames.SessionStart, OnSessionStartAsync));
        _subscriptions.Add(api.On(ExtensionEventNames.AgentStart, OnAgentStartAsync));

    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        return ValueTask.CompletedTask;
    }

    // --- lifecycle ---

    private Task OnSessionStartAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        // A new session gets one fresh first-turn injection.
        _mentalModelsInjected = false;
        _mentalModelCache = null;
        return Task.CompletedTask;
    }

    private async Task OnAgentStartAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        // Only refresh the cache here; the "has injected" decision belongs to the
        // contributor so it can mark the session only when it actually yields.
        var store = Store;
        if (store is null || store.Provider.Id == "off") return;

        var records = await store.RecallAsync(
            MemoryScope.Project,
            new MemoryQuery(Kind: MemoryKind.MentalModel, Limit: _settings.PromptMaxRecords),
            cancellationToken).ConfigureAwait(false);
        lock (_gate) _mentalModelCache = records;
    }

    private async Task OnSettingsChangedAsync(ExtensionSettingsChange change, CancellationToken cancellationToken)
    {
        var previous = _settings;
        _settings = MemorySettings.Read(_api?.Settings ?? throw new InvalidOperationException("Memory extension is not initialized."));
        if (_settings.Backend != previous.Backend && _api is not null)
        {
            var store = Store;
            if (store is not null)
            {
                var provider = ResolveBackend(_settings.Backend);
                var replacement = new MemoryStore(provider, store.ProjectKey);
                lock (_gate)
                {
                    _store = replacement;
                    // Rebuild the tool/command wiring so future calls hit the new
                    // store; registrations capture the fields, not the instances.
                    _coordinator = new MemoryToolCoordinator(replacement);
                    _commandHandler = new MemoryCommandHandler(_api, replacement, () => _settings);
                    _mentalModelCache = null;
                    _mentalModelsInjected = false;
                }
                MemoryServices.Store = replacement;
                await _api.EmitClientEventAsync(
                    MemoryEventNames.MemoryBackendChanged,
                    new { backend = _settings.Backend, previous = previous.Backend },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private IMemoryProvider ResolveBackend(string backend)
    {
        var provider = MemoryServices.Providers.TryGet(backend);
        if (provider is not null) return provider;
        return _fallbackOff ??= new DisabledMemoryProvider();
    }

    // --- registration ---

    private void RegisterTools(IExtensionApi api)
    {
        _subscriptions.Add(api.RegisterTool(CreateTool(
            "retain",
            "Upsert a memory record (fact, lesson, summary or mental-model). Idempotent when a stable recordKey is supplied; otherwise a slugified kind/timestamp key is generated. Project-scoped by default.",
            ToolSchemas.FromType<RetainToolInput>(),
            "retain")));
        _subscriptions.Add(api.RegisterTool(CreateTool(
            "recall",
            "Search stored memory records. Keyword-ranked on the file/sqlite backends, semantic on vector. Returns ranked records as markdown; omit the query to list records.",
            ToolSchemas.FromType<RecallToolInput>(),
            "recall")));
        _subscriptions.Add(api.RegisterTool(CreateTool(
            "reflect",
            "Read-only synthesis: gather related memory records (and recent session context) and instruct the model to synthesize them, then retain the result as kind=summary. Never writes.",
            ToolSchemas.FromType<ReflectToolInput>(),
            "reflect")));
        _subscriptions.Add(api.RegisterTool(CreateTool(
            "memory_edit",
            "Partially update a memory record (title/content/tags) or invalidate it (soft delete; invalidated records are hidden from default recall).",
            ToolSchemas.FromType<MemoryEditToolInput>(),
            "memory_edit")));
        _subscriptions.Add(api.RegisterTool(CreateTool(
            "learn",
            "Store a reusable lesson (kind=lesson). With promote=true and a skillName, additionally promote the lesson to a managed skill when the host provides a managed-skill store.",
            ToolSchemas.FromType<LearnToolInput>(),
            "learn")));

        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration(
            "memory",
            "Inspect memory: /memory (summary), /memory list [kind], /memory show <recordKey>, /memory forget <recordKey>, /memory backend.",
            (args, ct) => _commandHandler?.HandleAsync(args, ct) ?? Task.CompletedTask)));
    }

    private ExtensionToolRegistration CreateTool(string name, string description, System.Text.Json.JsonElement schema, string toolName)
        => new(
            name,
            name,
            description,
            schema,
            (toolCallId, parameters, ct, _) => _coordinator!.ExecuteAsync(toolName, parameters, ct));

    /// <summary>
    /// Built-in no-op backend used when <c>extensions.pisharp-memory.backend</c>
    /// names a provider that is not registered (backend plugin not loaded) or is
    /// "off". Tools answer with the blocked result.
    /// </summary>
    private sealed class DisabledMemoryProvider : IMemoryProvider
    {
        public string Id => "off";
        public string DisplayName => "Off (no backend registered)";
        public bool SupportsSemanticSearch => false;

        public Task<MemoryRecord?> GetAsync(MemoryScope scope, string recordKey, CancellationToken ct = default) => Task.FromResult<MemoryRecord?>(null);
        public Task PutAsync(MemoryScope scope, MemoryRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(MemoryScope scope, string recordKey, CancellationToken ct = default) => Task.FromResult(false);
        public Task<MemoryRecord?> UpdateAsync(MemoryScope scope, string recordKey, Func<MemoryRecord, MemoryRecord> mutate, CancellationToken ct = default) => Task.FromResult<MemoryRecord?>(null);
        public Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryRecord>>([]);
        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string text, int limit = 10, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);
        public Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryRecord>>([]);
    }
}
