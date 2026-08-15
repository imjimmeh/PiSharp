using System.Text.Json;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ai.Auth;

namespace PiSharp.Extensions;

public sealed record ExtensionResourceItem(string Kind, string Path);
public sealed record ExtensionResourceContent(string Path, string Content);

public sealed class ExtensionRuntimeBinding
{
    private readonly Dictionary<string, ExtensionFlagRegistration> _flagRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _flagValues = new(StringComparer.Ordinal);

    private static readonly Func<AgentMessage, ExtensionMessageDelivery, bool, CancellationToken, Task> DefaultSendMessageAsync
        = (_, _, _, _) => Task.CompletedTask;
    private static readonly Func<string, JsonElement, CancellationToken, Task<AgentToolResult<object?>>> DefaultExecuteToolByNameAsync
        = (name, _, _) => Task.FromResult(new AgentToolResult<object?>([new TextContent($"Tool '{name}' is not available in this extension host.")], null));
    public ExtensionRuntimeBinding(string cwd, bool hasUi, IExtensionUi ui)
    {
        Cwd = cwd;
        HasUi = hasUi;
        Ui = ui;
        Session = new BindingSessionApi(this);
        Tools = new BindingToolApi(this);
        Skills = new BindingSkillApi(this);
        Packages = new BindingPackageApi(this);
        Model = new BindingModelApi(this);
    }

    public string Cwd { get; }
    public bool HasUi { get; private set; }
    public IExtensionUi Ui { get; private set; }
    public IExtensionSessionApi Session { get; }
    public IExtensionToolApi Tools { get; }
    public IExtensionSkillApi Skills { get; }
    public IExtensionModelApi Model { get; }
    public IExtensionPackageApi Packages { get; }
    public IExtensionRuntimeSettings? RuntimeSettings { get; set; }
    public IExtensionRuntimeState? RuntimeState { get; set; }
    public IExecutionEnv? ExecutionEnv { get; set; }
    public IOAuthStorage? RuntimeAuthStorage { get; set; }
    public InternalUrlRegistry? UrlRegistry { get; set; }
    public FileContentExtractorRegistry? FileContentExtractors { get; set; }
    public SearchProviderRegistry? SearchProviders { get; set; }
    public IExtensionTelemetryApi Telemetry { get; set; } = NoOpTelemetryApi.Instance;
    public Func<ExtensionSessionReplacementResult, IExtensionReplacementSessionApi?, CancellationToken, Task>? WithSessionCallback { get; set; }
    public Func<AgentMessage, ExtensionMessageDelivery, bool, CancellationToken, Task> SendMessageAsync { get; set; } = DefaultSendMessageAsync;
    public Func<string, ExtensionMessageDelivery, CancellationToken, Task> SendUserMessageAsync { get; set; } = (_, _, _) => Task.CompletedTask;
    public Func<string, object, CancellationToken, Task> AppendEntryAsync { get; set; } = (_, _, _) => Task.CompletedTask;
    public Func<string, object, bool, object?, CancellationToken, Task>? AppendCustomMessageEntryAsync { get; set; }
    public Func<CancellationToken, Task<string?>> GetSessionIdAsync { get; set; } = _ => Task.FromResult<string?>(null);
    public Func<CancellationToken, Task<string?>> GetSessionNameAsync { get; set; } = _ => Task.FromResult<string?>(null);
    public Func<CancellationToken, Task<object?>> GetSessionSnapshotAsync { get; set; } = _ => Task.FromResult<object?>(null);
    public Func<string, CancellationToken, Task> SetSessionNameAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, string?, CancellationToken, Task> SetLabelAsync { get; set; } = (_, _, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<IReadOnlyList<string>>> GetActiveToolsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public Func<CancellationToken, Task<IReadOnlyList<string>>> GetAllToolsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public Func<CancellationToken, Task<IReadOnlyList<ExtensionCommandInfo>>> GetCommandsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<ExtensionCommandInfo>>([]);
    public Func<CancellationToken, Task> WaitForIdleAsync { get; set; } = _ => Task.CompletedTask;
    public Func<Func<ExtensionSessionReplacementResult, IExtensionReplacementSessionApi?, CancellationToken, Task>?, CancellationToken, Task<ExtensionSessionReplacementResult>> NewSessionAsync { get; set; }
        = (_, _) => Task.FromResult(new ExtensionSessionReplacementResult(true, "Extension runtime is not bound."));
    public Func<string?, string?, Func<ExtensionSessionReplacementResult, IExtensionReplacementSessionApi?, CancellationToken, Task>?, CancellationToken, Task<ExtensionSessionReplacementResult>> ForkSessionAsync { get; set; }
        = (_, _, _, _) => Task.FromResult(new ExtensionSessionReplacementResult(true, "Extension runtime is not bound."));
    public Func<string, CancellationToken, Task> NavigateTreeAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, Func<ExtensionSessionReplacementResult, IExtensionReplacementSessionApi?, CancellationToken, Task>?, CancellationToken, Task<ExtensionSessionReplacementResult>> SwitchSessionAsync { get; set; }
        = (_, _, _) => Task.FromResult(new ExtensionSessionReplacementResult(true, "Extension runtime is not bound."));
    public Func<CancellationToken, Task<bool>> IsIdleAsync { get; set; } = _ => Task.FromResult(true);
    public Func<CancellationToken, Task<bool>> HasPendingMessagesAsync { get; set; } = _ => Task.FromResult(false);
    public Func<string, JsonElement, CancellationToken, Task<AgentToolResult<object?>>> ExecuteToolByNameAsync { get; set; } = DefaultExecuteToolByNameAsync;
    public Func<string, string, string, ExtensionCompleteRequest?, CancellationToken, Task<ExtensionCompletionResult>> CompleteSimpleAsync { get; set; }
        = (_, _, _, _, _) => Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Error, null, "Extension runtime is not bound.", null));
    public Func<string, string, IReadOnlyList<AgentMessage>?, string?, ExtensionCompleteRequest?, bool, CancellationToken, Task<ExtensionCompletionResult>> CompleteAsync { get; set; }
        = (_, _, _, _, _, _, _) => Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Error, null, "Extension runtime is not bound.", null));
    public Func<string, string, IReadOnlyList<AgentMessage>?, string?, ExtensionCompleteRequest?, bool, CancellationToken, IAsyncEnumerable<ExtensionCompletionDelta>> StreamAsync { get; set; }
        = (_, _, _, _, _, _, _) => EmptyCompletionStream();
    public Func<IReadOnlyList<string>?, CancellationToken, Task> SetActiveToolsAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<IReadOnlyList<ExtensionSkillDefinition>>> GetAllSkillsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>([]);
    public Func<ISkillProvider, CancellationToken, Task<IDisposable>> RegisterSkillProviderAsync { get; set; }
        = (_, _) => Task.FromResult<IDisposable>(new DisposableAction(() => { }));
    public Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> GetSkillProviderPrioritiesAsync { get; set; }
        = _ => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    public Func<string, bool, bool, bool, CancellationToken, Task<ExtensionPackageResult>> InstallExtensionAsync { get; set; }
        = (_, _, _, _, _) => Task.FromResult(new ExtensionPackageResult(false, "Extension package API is not bound."));
    public Func<ExtensionPackageUpdateRequest, CancellationToken, Task<ExtensionPackageResult>> UpdateExtensionAsync { get; set; }
        = (_, _) => Task.FromResult(new ExtensionPackageResult(false, "Extension package API is not bound."));
    public Func<string, bool, CancellationToken, Task<bool>> RemoveExtensionAsync { get; set; } = (_, _, _) => Task.FromResult(false);
    public Func<CancellationToken, Task<IReadOnlyList<ExtensionInstalledPackage>>> ListInstalledExtensionsAsync { get; set; }
        = _ => Task.FromResult<IReadOnlyList<ExtensionInstalledPackage>>([]);
    public Func<ManagedSkillCreateRequest, CancellationToken, Task<ManagedSkillDescriptor>> ManagedSkillCreateAsync { get; set; }
        = (_, _) => Task.FromException<ManagedSkillDescriptor>(new NotSupportedException("Managed skill store is not bound."));
    public Func<string, ManagedSkillUpdateRequest, CancellationToken, Task<ManagedSkillDescriptor>> ManagedSkillUpdateAsync { get; set; }
        = (_, _, _) => Task.FromException<ManagedSkillDescriptor>(new NotSupportedException("Managed skill store is not bound."));
    public Func<string, CancellationToken, Task<bool>> ManagedSkillDeleteAsync { get; set; } = (_, _) => Task.FromResult(false);
    public Func<CancellationToken, Task<IReadOnlyList<ManagedSkillDescriptor>>> ManagedSkillListAsync { get; set; }
        = _ => Task.FromResult<IReadOnlyList<ManagedSkillDescriptor>>([]);
    public Func<string, CancellationToken, Task<ManagedSkillDescriptor>> ManagedSkillPromoteAsync { get; set; }
        = (_, _) => Task.FromException<ManagedSkillDescriptor>(new NotSupportedException("Managed skill store is not bound."));
    public Func<CancellationToken, Task<IReadOnlyList<string>>> GetSelectedSkillsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public Func<IReadOnlyList<string>, CancellationToken, Task> SetSelectedSkillsAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<IRuleProvider, CancellationToken, Task<IDisposable>> RegisterRuleProviderAsync { get; set; }
        = (_, _) => Task.FromResult<IDisposable>(new DisposableAction(() => { }));
    public Func<CancellationToken, Task<IReadOnlyList<Rule>>> GetAllRulesAsync { get; set; }
        = _ => Task.FromResult<IReadOnlyList<Rule>>([]);
    public Func<CancellationToken, Task<IReadOnlyList<string>>> GetRuleProviderNamesAsync { get; set; }
        = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public Func<string?, CancellationToken, Task> CompactAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<string>> GetSystemPromptAsync { get; set; } = _ => Task.FromResult(string.Empty);
    public Func<CancellationToken, Task> AbortAsync { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task> ShutdownAsync { get; set; } = _ => Task.CompletedTask;
    public Func<ModelDescriptor, CancellationToken, Task<bool>> SetModelAsync { get; set; } = (_, _) => Task.FromResult(false);
    public Func<CancellationToken, Task<ModelDescriptor?>> GetModelAsync { get; set; } = _ => Task.FromResult<ModelDescriptor?>(null);
    public Func<CancellationToken, Task<ThinkingLevel?>> GetThinkingLevelAsync { get; set; } = _ => Task.FromResult<ThinkingLevel?>(null);
    public Func<ThinkingLevel, CancellationToken, Task> SetThinkingLevelAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, CancellationToken, Task<ExtensionModelSelection?>> ResolveModelRoleAsync { get; set; } = (_, _) => Task.FromResult<ExtensionModelSelection?>(null);
    public Func<string, CancellationToken, Task<bool>> SetModelByRoleAsync { get; set; } = (_, _) => Task.FromResult(false);
    public Func<CancellationToken, Task> ReloadExtensionsAsync { get; set; } = _ => Task.CompletedTask;
    public Func<string, object?, CancellationToken, Task> EmitEventAsync { get; set; } = (_, _, _) => Task.CompletedTask;
    public Func<string, object?, CancellationToken, Task> EmitClientEventAsync { get; set; } = (_, _, _) => Task.CompletedTask;
    // [daemon: deferred until P01] Theme delegates — wired by RuntimeExtensionBinder for in-process mode.
    public Func<CancellationToken, Task<IReadOnlyList<ExtensionThemeInfo>>> GetAllThemesAsync { get; set; }
        = _ => Task.FromResult<IReadOnlyList<ExtensionThemeInfo>>([]);
    public Func<CancellationToken, Task<ExtensionThemeInfo?>> GetThemeAsync { get; set; }
        = _ => Task.FromResult<ExtensionThemeInfo?>(null);
    public Func<string, CancellationToken, Task> SetThemeAsync { get; set; }
        = (_, _) => Task.CompletedTask;
    /// <summary>
    /// Raised when the active theme changes in-process; the daemon-side theme event lands in P01.
    /// </summary>
    public event EventHandler? ThemeChanged;
    public void RaiseThemeChanged() => ThemeChanged?.Invoke(this, EventArgs.Empty);
    public Func<object?, CancellationToken, Task<object?>> CreateAgentSessionAsync { get; set; } = (_, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, string, object?, CancellationToken, Task<object?>> AgentSessionPromptAsync { get; set; } = (_, _, _, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, string, CancellationToken, Task<object?>> AgentSessionSteerAsync { get; set; } = (_, _, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, string, CancellationToken, Task<object?>> AgentSessionFollowUpAsync { get; set; } = (_, _, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, CancellationToken, Task<object?>> AgentSessionAbortAsync { get; set; } = (_, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, string?, CancellationToken, Task<object?>> AgentSessionCompactAsync { get; set; } = (_, _, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, ModelDescriptor, CancellationToken, Task<object?>> AgentSessionSetModelAsync { get; set; } = (_, _, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, ThinkingLevel, CancellationToken, Task<object?>> AgentSessionSetThinkingLevelAsync { get; set; } = (_, _, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, CancellationToken, Task<object?>> AgentSessionDisposeAsync { get; set; } = (_, _) => Task.FromResult<object?>(new { ok = false, error = "Subagent session service is not bound." });
    public Func<string, object, CancellationToken, Task>? OnChildSessionEventAsync { get; set; }

    public IReadOnlyList<ExtensionResourceItem> ResourceItems { get; set; } = [];
    public Func<string, CancellationToken, Task<ExtensionResourceContent?>> ReadResourceAsync { get; set; }
        = (_, _) => Task.FromResult<ExtensionResourceContent?>(null);

    public IReadOnlyDictionary<string, object?> FlagValues => new Dictionary<string, object?>(_flagValues, StringComparer.Ordinal);
    public object? GetFlag(string name) => _flagValues.TryGetValue(name, out var value) ? value : null;

    public void RegisterFlag(ExtensionFlagRegistration registration)
    {
        _flagRegistrations[registration.Name] = registration;
        if (!_flagValues.ContainsKey(registration.Name) && registration.DefaultValue is not null) _flagValues[registration.Name] = registration.DefaultValue;
    }

    public bool TrySetFlagValue(string name, object? value, out string? error)
    {
        error = null;
        if (!_flagRegistrations.TryGetValue(name, out var registration))
        {
            error = $"Unknown extension flag '--{name}'.";
            return false;
        }

        if (registration.Type == ExtensionFlagType.String)
        {
            if (value is not string text || string.IsNullOrEmpty(text))
            {
                error = $"Extension flag '--{name}' requires a value.";
                return false;
            }
            _flagValues[name] = text;
            return true;
        }

        _flagValues[name] = value is bool boolean ? boolean : true;
        return true;
    }

    public void SetUi(IExtensionUi ui, bool hasUi)
    {
        Ui = ui;
        HasUi = hasUi;
    }

    /// <summary>
    /// Throws when any core capability is still on its no-op default. Core capabilities are
    /// <see cref="ExecutionEnv"/>, <see cref="SendMessageAsync"/> and
    /// <see cref="ExecuteToolByNameAsync"/>: without them an extension silently no-ops (empty
    /// messages, "not available" tool results) and the failure hides. The host MUST call
    /// <see cref="BindingsComplete"/> after wiring these before any extension is initialized.
    /// </summary>
    public void ValidateBound()
    {
        var missing = new List<string>();
        if (ExecutionEnv is null) missing.Add(nameof(ExecutionEnv));
        if (ReferenceEquals(SendMessageAsync, DefaultSendMessageAsync)) missing.Add(nameof(SendMessageAsync));
        if (ReferenceEquals(ExecuteToolByNameAsync, DefaultExecuteToolByNameAsync)) missing.Add(nameof(ExecuteToolByNameAsync));
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Extension runtime binding was not fully wired before use; missing core capabilities: {string.Join(", ", missing)}. " +
                "This is a host wiring bug — an unbound binding silently no-ops instead of failing.");
        }
    }

    /// <summary>Marks the binding as fully wired, converting a silent no-op into a startup error.</summary>
    public void BindingsComplete()
    {
        ValidateBound();
    }

    public ExtensionRuntimeActions ToActions() => new(Cwd, HasUi, Ui,
        (message, token) => SendMessageAsync(message, ExtensionMessageDelivery.NextTurn, false, token),
        GetSessionNameAsync,
        async (model, token) => { await SetModelAsync(model, token); },
        SetThinkingLevelAsync);

    private sealed class BindingSessionApi(ExtensionRuntimeBinding binding) : IExtensionSessionApi
    {
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
            => binding.SendMessageAsync(message, delivery, triggerTurn, cancellationToken);
        public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
            => binding.SendUserMessageAsync(content, delivery, cancellationToken);
        public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default)
            => binding.AppendEntryAsync(customType, data, cancellationToken);
        public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) => binding.GetSessionNameAsync(cancellationToken);
        public Task SetNameAsync(string name, CancellationToken cancellationToken = default) => binding.SetSessionNameAsync(name, cancellationToken);
        public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default) => binding.SetLabelAsync(entryId, label, cancellationToken);
        public Task<ExtensionSessionReplacementResult> NewSessionAsync(
            Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null,
            CancellationToken cancellationToken = default)
            => binding.NewSessionAsync(AdaptWithSession(withSession), cancellationToken);
        public Task<ExtensionSessionReplacementResult> ForkAsync(
            string? entryId = null,
            string? position = "before",
            Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null,
            CancellationToken cancellationToken = default)
            => binding.ForkSessionAsync(entryId, position, AdaptWithSession(withSession), cancellationToken);
        public Task<ExtensionSessionReplacementResult> SwitchSessionAsync(
            string sessionPathOrId,
            Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null,
            CancellationToken cancellationToken = default)
            => binding.SwitchSessionAsync(sessionPathOrId, AdaptWithSession(withSession), cancellationToken);
        public Task NavigateTreeAsync(string targetId, bool summarize = false, CancellationToken cancellationToken = default)
            => binding.NavigateTreeAsync(targetId, cancellationToken);
        public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
            => binding.WaitForIdleAsync(cancellationToken);
        public Task<bool> IsIdleAsync(CancellationToken cancellationToken = default)
            => binding.IsIdleAsync(cancellationToken);
        public Task<bool> HasPendingMessagesAsync(CancellationToken cancellationToken = default)
            => binding.HasPendingMessagesAsync(cancellationToken);
    }

    private sealed class BindingToolApi(ExtensionRuntimeBinding binding) : IExtensionToolApi
    {
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => new DisposableAction(() => { });
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default) => binding.GetActiveToolsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default) => binding.GetAllToolsAsync(cancellationToken);
        public Task SetActiveToolsAsync(IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default) => binding.SetActiveToolsAsync(toolNames, cancellationToken);
        public Task<AgentToolResult<object?>> ExecuteToolAsync(string toolName, JsonElement parameters, CancellationToken cancellationToken = default)
            => binding.ExecuteToolByNameAsync(toolName, parameters, cancellationToken);
    }

    private sealed class BindingSkillApi(ExtensionRuntimeBinding binding) : IExtensionSkillApi
    {
        public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => new DisposableAction(() => { });
        public IDisposable RegisterSkillProvider(ISkillProvider provider)
            => binding.RegisterSkillProviderAsync(provider, CancellationToken.None).GetAwaiter().GetResult();
        public Task<IReadOnlyList<ExtensionSkillDefinition>> GetAllSkillsAsync(CancellationToken cancellationToken = default) => binding.GetAllSkillsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default) => binding.GetSelectedSkillsAsync(cancellationToken);
        public Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default) => binding.SetSelectedSkillsAsync(skillNames, cancellationToken);
        public IExtensionManagedSkillApi ManagedSkills { get; } = new BindingManagedSkillApi(binding);
    }

    private sealed class BindingPackageApi(ExtensionRuntimeBinding binding) : IExtensionPackageApi
    {
        public Task<ExtensionPackageResult> InstallAsync(string reference, bool local = false, bool force = false, bool offline = false, CancellationToken ct = default)
            => binding.InstallExtensionAsync(reference, local, force, offline, ct);
        public Task<ExtensionPackageResult> UpdateAsync(ExtensionPackageUpdateRequest request, CancellationToken ct = default)
            => binding.UpdateExtensionAsync(request, ct);
        public Task<bool> RemoveAsync(string reference, bool local = false, CancellationToken ct = default)
            => binding.RemoveExtensionAsync(reference, local, ct);
        public Task<IReadOnlyList<ExtensionInstalledPackage>> ListAsync(CancellationToken ct = default)
            => binding.ListInstalledExtensionsAsync(ct);
    }

    private sealed class BindingManagedSkillApi(ExtensionRuntimeBinding binding) : IExtensionManagedSkillApi
    {
        public Task<ManagedSkillDescriptor> CreateAsync(ManagedSkillCreateRequest request, CancellationToken ct = default)
            => binding.ManagedSkillCreateAsync(request, ct);
        public Task<ManagedSkillDescriptor> UpdateAsync(string name, ManagedSkillUpdateRequest request, CancellationToken ct = default)
            => binding.ManagedSkillUpdateAsync(name, request, ct);
        public Task<bool> DeleteAsync(string name, CancellationToken ct = default)
            => binding.ManagedSkillDeleteAsync(name, ct);
        public Task<IReadOnlyList<ManagedSkillDescriptor>> ListAsync(CancellationToken ct = default)
            => binding.ManagedSkillListAsync(ct);
        public Task<ManagedSkillDescriptor> PromoteAsync(string sourceReference, CancellationToken ct = default)
            => binding.ManagedSkillPromoteAsync(sourceReference, ct);
    }

    private sealed class BindingModelApi(ExtensionRuntimeBinding binding) : IExtensionModelApi
    {
        public Task<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default) => binding.SetModelAsync(model, cancellationToken);
        public Task<ModelDescriptor?> GetModelAsync(CancellationToken cancellationToken = default) => binding.GetModelAsync(cancellationToken);
        public Task<ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default) => binding.GetThinkingLevelAsync(cancellationToken);
        public Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default) => binding.SetThinkingLevelAsync(level, cancellationToken);
        public Task<ExtensionModelSelection?> ResolveRoleAsync(string role, CancellationToken cancellationToken = default) => binding.ResolveModelRoleAsync(role, cancellationToken);
        public Task<bool> SetModelByRoleAsync(string role, CancellationToken cancellationToken = default) => binding.SetModelByRoleAsync(role, cancellationToken);
    }

    private sealed class ReplacementSessionApi(
        string sessionId,
        string? sessionFile,
        Func<AgentMessage, ExtensionMessageDelivery, bool, CancellationToken, Task> sendMessageAsync,
        Func<string, ExtensionMessageDelivery, CancellationToken, Task> sendUserMessageAsync) : IExtensionReplacementSessionApi
    {
        public string SessionId { get; } = sessionId;
        public string? SessionFile { get; } = sessionFile;
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.NextTurn, bool triggerTurn = false, CancellationToken cancellationToken = default)
            => sendMessageAsync(message, delivery, triggerTurn, cancellationToken);
        public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
            => sendUserMessageAsync(content, delivery, cancellationToken);
    }

    public IExtensionReplacementSessionApi CreateReplacementSessionApi(ExtensionSessionReplacementResult result, Func<AgentMessage, ExtensionMessageDelivery, bool, CancellationToken, Task> sendMessageAsync, Func<string, ExtensionMessageDelivery, CancellationToken, Task> sendUserMessageAsync)
        => new ReplacementSessionApi(result.SessionId ?? string.Empty, result.SessionFile, sendMessageAsync, sendUserMessageAsync);

    private static Func<ExtensionSessionReplacementResult, IExtensionReplacementSessionApi?, CancellationToken, Task>? AdaptWithSession(Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession)
        => withSession is null ? null : (_, replacement, token) => withSession(replacement!, token);

    private static async IAsyncEnumerable<ExtensionCompletionDelta> EmptyCompletionStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
