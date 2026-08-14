using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Agent.Serialization;
using PiSharp.Ai.Models;
using PiSharp.Compatibility.Resources;
using PiSharp.Extensions;
using PiSharp.Compatibility.Settings;
using PiSharp.Runtime.Subagents;
using PiSharp.Packages;
using ModelDescriptor = PiSharp.Agent.Core.Models.ModelDescriptor;

namespace PiSharp.Runtime;

internal sealed class RuntimeExtensionBinder(ExtensionManager? extensionManager) : IDisposable, IAsyncDisposable
{
    private SessionRuntime? _runtime;
    private bool _runtimeActionsBound;
    private SubagentSessionService? _subagentService;
    private readonly Dictionary<string, IDisposable> _subagentEventSubscriptions = new(StringComparer.Ordinal);
    private RuntimeSessionSnapshotCacheEntry? _sessionSnapshotCache;
    private readonly Dictionary<string, IAgentTool> _toolsByName = new(StringComparer.Ordinal);
    private int _evalLoopbackCallCounter;
    private RuntimePackageService? _packageService;
    private ManagedSkillStore? _managedSkillStore;
    private Task? _managedSkillLoadTask;

    public void BindRuntimeActions(SessionRuntime runtime)
    {
        if (_runtimeActionsBound) return;
        _runtimeActionsBound = true;
        _runtime = runtime;
        _subagentService = new SubagentSessionService(runtime);

        var binding = runtime.ExtensionBinding;
        binding.RuntimeSettings = runtime.SettingsService;
        binding.SendMessageAsync = runtime.SendExtensionMessageAsync;
        binding.RuntimeAuthStorage = runtime.AuthStorage;
        binding.SendUserMessageAsync = (content, delivery, token) =>
            runtime.SendExtensionMessageAsync(AgentMessages.User(content), delivery, triggerTurn: delivery == ExtensionMessageDelivery.NextTurn, token);
        binding.GetSessionIdAsync = _ => Task.FromResult<string?>(runtime.Session.Metadata.Id);
        binding.GetSessionNameAsync = token => runtime.Session.GetSessionNameAsync(token);
        binding.GetSessionSnapshotAsync = token => GetSessionSnapshotAsync(runtime, token);
        binding.SetSessionNameAsync = async (name, token) => { await runtime.Harness.SetSessionNameAsync(name, token); };
        binding.AppendEntryAsync = async (type, data, token) => { await runtime.Session.AppendCustomEntryAsync($"extension:runtime:{type}", data, token); };
        binding.AppendCustomMessageEntryAsync = async (customType, content, display, details, token) =>
        {
            await runtime.Session.AppendCustomMessageEntryAsync(customType, content, display, details, token);
        };
        binding.SetLabelAsync = async (entryId, label, token) => { await runtime.Session.AppendLabelAsync(entryId, label, token); };
        binding.ReloadExtensionsAsync = runtime.ReloadExtensionsAsync;
        binding.WaitForIdleAsync = token => runtime.Harness.WaitForIdleAsync().WaitAsync(token);
        binding.NewSessionAsync = async (withSession, token) =>
        {
            var result = ToExtensionSessionReplacementResult(await runtime.NewSessionAsync(token));
            await InvokeWithSessionAsync(binding, runtime, result, withSession, token);
            return result;
        };
        binding.ForkSessionAsync = async (entryId, position, withSession, token) =>
        {
            var result = ToExtensionSessionReplacementResult(await runtime.ForkAsync(runtime.Session.Metadata, new SessionForkOptions(entryId, position ?? "before"), token));
            await InvokeWithSessionAsync(binding, runtime, result, withSession, token);
            return result;
        };
        binding.CreateAgentSessionAsync = async (optionsObj, token) =>
        {
            var options = ParseSubagentOptions(optionsObj);
            var handle = await _subagentService.CreateAsync(options, token);
            var snapshot = await BuildSubagentSnapshotAsync(handle, token);

            var subscription = _subagentService.Subscribe(handle.SessionId, async (evt, ct) =>
            {
                if (binding.OnChildSessionEventAsync is not null)
                    await binding.OnChildSessionEventAsync(handle.SessionId, evt, ct);
            });
            ReplaceSubagentEventSubscription(handle.SessionId, subscription);

            return new { ok = true, sessionId = handle.SessionId, session = snapshot, extensionsResult = new { extensions = Array.Empty<object>(), diagnostics = Array.Empty<object>() }, modelFallbackMessage = (string?)null };
        };
        binding.AgentSessionPromptAsync = async (sessionId, content, optionsObj, token) =>
        {
            var result = await _subagentService.PromptAsync(sessionId, content, token);
            var snapshot = await BuildSubagentSnapshotAsync(_subagentService.GetHandle(sessionId)!, token);
            return new { ok = true, sessionId = result.SessionId, message = result.FinalMessage, finalMessage = result.FinalMessage, session = snapshot };
        };
        binding.AgentSessionSteerAsync = async (sessionId, content, token) =>
        {
            await _subagentService.SteerAsync(sessionId, content, token);
            return new { ok = true, sessionId };
        };
        binding.AgentSessionFollowUpAsync = async (sessionId, content, token) =>
        {
            await _subagentService.FollowUpAsync(sessionId, content, token);
            return new { ok = true, sessionId };
        };
        binding.AgentSessionAbortAsync = async (sessionId, token) =>
        {
            await _subagentService.AbortAsync(sessionId, token);
            return new { ok = true, sessionId };
        };
        binding.AgentSessionCompactAsync = async (sessionId, instructions, token) =>
        {
            await _subagentService.CompactAsync(sessionId, instructions, token);
            var snapshot = await BuildSubagentSnapshotAsync(_subagentService.GetHandle(sessionId)!, token);
            return new { ok = true, sessionId, session = snapshot };
        };
        binding.AgentSessionSetModelAsync = async (sessionId, model, token) =>
        {
            await _subagentService.SetModelAsync(sessionId, model, token);
            var snapshot = await BuildSubagentSnapshotAsync(_subagentService.GetHandle(sessionId)!, token);
            return new { ok = true, sessionId, session = snapshot };
        };
        binding.AgentSessionSetThinkingLevelAsync = async (sessionId, level, token) =>
        {
            await _subagentService.SetThinkingLevelAsync(sessionId, level, token);
            var snapshot = await BuildSubagentSnapshotAsync(_subagentService.GetHandle(sessionId)!, token);
            return new { ok = true, sessionId, session = snapshot };
        };
        binding.AgentSessionDisposeAsync = async (sessionId, token) =>
        {
            RemoveSubagentEventSubscription(sessionId);
            await _subagentService.DisposeAsync(sessionId, token);
            return new { ok = true, sessionId };
        };
        binding.NavigateTreeAsync = async (targetId, token) => await runtime.Harness.NavigateTreeAsync(targetId, cancellationToken: token);
        binding.SwitchSessionAsync = async (sessionPath, withSession, token) =>
        {
            var sessions = await runtime.ListSessionsAsync(cancellationToken: token);
            var target = sessions.FirstOrDefault(session => string.Equals(session.Path, sessionPath, StringComparison.OrdinalIgnoreCase) || string.Equals(session.Id, sessionPath, StringComparison.OrdinalIgnoreCase));
            var result = target is null
                ? new ExtensionSessionReplacementResult(true, $"Session '{sessionPath}' was not found.")
                : ToExtensionSessionReplacementResult(await runtime.SwitchSessionAsync(target, token));
            await InvokeWithSessionAsync(binding, runtime, result, withSession, token);
            return result;
        };
        binding.IsIdleAsync = _ => Task.FromResult(runtime.Harness.Phase == AgentHarnessPhase.Idle);
        binding.HasPendingMessagesAsync = _ => Task.FromResult(false);
        binding.CompactAsync = async (instructions, token) => await runtime.Harness.CompactAsync(instructions, token);
        binding.GetSystemPromptAsync = _ => Task.FromResult(runtime.Harness.LastPromptDocument is null ? string.Empty : PiSharp.Agent.Resources.Prompting.MarkdownSystemPromptRenderer.Default.Render(runtime.Harness.LastPromptDocument));
        binding.AbortAsync = _ => { runtime.Harness.Abort(); return Task.CompletedTask; };
        binding.ShutdownAsync = runtime.ShutdownFromExtensionAsync;
        binding.GetAllToolsAsync = _ => Task.FromResult<IReadOnlyList<string>>(runtime.Harness.AllToolNames);
        binding.GetActiveToolsAsync = _ => Task.FromResult<IReadOnlyList<string>>(runtime.Harness.ActiveToolNames);
        binding.ExecuteToolByNameAsync = ExecuteToolByNameAsync;
        var completionService = new ExtensionCompletionService();
        binding.CompleteSimpleAsync = (provider, modelId, prompt, options, token) => completionService.CompleteSimpleAsync(provider, modelId, prompt, options, token);
        binding.CompleteAsync = (provider, modelId, messages, systemPrompt, options, streamFullOnTimeout, token) => completionService.CompleteAsync(provider, modelId, messages, systemPrompt, options, streamFullOnTimeout, token);
        binding.StreamAsync = (provider, modelId, messages, systemPrompt, options, streamFullOnTimeout, token) => completionService.StreamAsync(provider, modelId, messages, systemPrompt, options, streamFullOnTimeout, token);
        binding.SetActiveToolsAsync = (names, _) => { runtime.Harness.SetActiveTools(names ?? []); return Task.CompletedTask; };
        binding.GetAllSkillsAsync = _ => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>(
            runtime.Harness.Skills.Select(ToExtensionSkillDefinition).ToArray());
        binding.GetSelectedSkillsAsync = _ => Task.FromResult<IReadOnlyList<string>>(runtime.Harness.SelectedSkillNames);
        binding.SetSelectedSkillsAsync = (names, _) => { runtime.Harness.SetSelectedSkills(names); return Task.CompletedTask; };
        binding.RegisterSkillProviderAsync = (provider, ct) => Task.FromResult<IDisposable>(
            extensionManager is null
                ? new NoopDisposable()
                : extensionManager.Registry.RegisterSkillProvider("runtime", provider));
        binding.GetSkillProviderPrioritiesAsync = _ => Task.FromResult<IReadOnlyDictionary<string, int>>(
            extensionManager is null
                ? new Dictionary<string, int>()
                : extensionManager.Registry.SkillProviders.ToDictionary(provider => provider.Value.Name, provider => provider.Value.Priority));
        var packageService = new RuntimePackageService(
            runnerFactory: CreatePackageRunnerFactory(runtime),
            reloadExtensionsAsync: runtime.ReloadExtensionsAsync,
            emitEventAsync: runtime.EmitRuntimeExtensionEventAsync);
        _packageService = packageService;
        binding.InstallExtensionAsync = (reference, local, force, offline, ct) => packageService.InstallAsync(reference, local, force, offline, ct);
        binding.UpdateExtensionAsync = (request, ct) => packageService.UpdateAsync(request, ct);
        binding.RemoveExtensionAsync = (reference, local, ct) => packageService.RemoveAsync(reference, local, ct);
        binding.ListInstalledExtensionsAsync = (ct) => packageService.ListAsync(ct);
        var managedRoot = Path.Combine(
            (runtime.SettingsSnapshot?.Paths ?? PiAgentPaths.FromCwd(binding.Cwd)).GlobalPiSharpDirectory,
            "managed-skills");
        var managedSkillStore = new ManagedSkillStore(
            managedRoot,
            extensionManager?.Registry,
            runtime.EmitRuntimeExtensionEventAsync,
            loggerFactory: null);
        _managedSkillStore = managedSkillStore;
        _managedSkillLoadTask = managedSkillStore.LoadAsync();
        binding.ManagedSkillCreateAsync = (request, ct) => managedSkillStore.CreateAsync(request, ct);
        binding.ManagedSkillUpdateAsync = (name, request, ct) => managedSkillStore.UpdateAsync(name, request, ct);
        binding.ManagedSkillDeleteAsync = (name, ct) => managedSkillStore.DeleteAsync(name, ct);
        binding.ManagedSkillListAsync = (ct) => managedSkillStore.ListAsync(ct);
        binding.ManagedSkillPromoteAsync = (reference, ct) => managedSkillStore.PromoteAsync(reference, ct);
        binding.GetCommandsAsync = _ => Task.FromResult(BuildCommandInfos(runtime));
        binding.SetModelAsync = async (model, token) => { await runtime.SetModelAsync(model, "extension", token); return true; };
        binding.GetModelAsync = _ => Task.FromResult<ModelDescriptor?>(runtime.Harness.Model);
        binding.GetThinkingLevelAsync = _ => Task.FromResult<ThinkingLevel?>(runtime.Harness.ThinkingLevel);
        binding.SetThinkingLevelAsync = runtime.SetThinkingLevelAsync;
        binding.ResolveModelRoleAsync = (role, token) => runtime.ResolveRoleAsync(role, token);
        binding.EmitClientEventAsync = (eventName, payload, token) =>
            runtime.Harness.PublishOwnEventAsync(new AgentHarnessOwnEvent.CustomEvent(eventName, payload), token);
        binding.SetModelByRoleAsync = (role, token) => runtime.SetModelByRoleAsync(role, token);
        binding.GetAllThemesAsync = _ => Task.FromResult(LoadAllThemes(runtime));
        binding.GetThemeAsync = _ => Task.FromResult<ExtensionThemeInfo?>(ToExtensionThemeInfo(runtime.Theme));
        binding.SetThemeAsync = async (name, token) => await runtime.SetThemeByNameAsync(name, token);
        runtime.ThemeChanged += OnRuntimeThemeChanged;
        foreach (var tool in runtime.Tools) _toolsByName[tool.Name] = tool;

        if (extensionManager is not null)
        {
            foreach (var registration in extensionManager.Registry.Tools) _toolsByName[registration.Value.Name] = registration.Value;
            ReplayTools(runtime.Harness);
            ReplaySkills(runtime.Harness);
            extensionManager.Registry.Changed += ApplyExtensionRegistryChangeAsync;
        }

        PopulateResourceBinding(binding, runtime);
    }

    public void ReplayTools(AgentHarness<JsonlSessionMetadata> harness)
    {
        if (extensionManager is null) return;
        foreach (var tool in extensionManager.Registry.Tools) harness.RegisterTool(tool.SourceId, tool.Value);
    }

    public void ReplaySkills(AgentHarness<JsonlSessionMetadata> harness)
    {
        if (extensionManager is null) return;
        foreach (var skill in extensionManager.Registry.Skills) harness.RegisterSkill(skill.SourceId, skill.Value);
    }

    public IDisposable? BindHarnessDispatch(AgentHarness<JsonlSessionMetadata> harness)
        => extensionManager is null || harness.HasExtensionRegistry
            ? null
            : harness.Subscribe((evt, token) => extensionManager.Registry.DispatchAsync(evt, token));

    private Task ApplyExtensionRegistryChangeAsync(ExtensionRegistryChange change, CancellationToken cancellationToken)
    {
        if (_runtime is null) return Task.CompletedTask;
        _sessionSnapshotCache = null;

        if (change.Kind == ExtensionRegistryChangeKind.SourceRemoved)
        {
            _runtime.Harness.UnregisterToolsBySource(change.SourceId);
            _runtime.Harness.UnregisterSkillsBySource(change.SourceId);
            RefreshResourceBinding();
            return Task.CompletedTask;
        }

        if (change.Category == "tool" && change.Value is IAgentTool tool)
        {
            if (change.Kind is ExtensionRegistryChangeKind.Added or ExtensionRegistryChangeKind.Replaced or ExtensionRegistryChangeKind.Restored)
            {
                _toolsByName[tool.Name] = tool;
                _runtime.Harness.RegisterTool(change.SourceId, tool);
            }
            else if (change.Kind == ExtensionRegistryChangeKind.Removed)
            {
                _toolsByName.Remove(tool.Name);
                _runtime.Harness.UnregisterTool(change.SourceId, tool.Name);
            }
        }

        if (change.Category == "skill" && change.Value is ExtensionSkillRegistration skill)
        {
            if (change.Kind is ExtensionRegistryChangeKind.Added or ExtensionRegistryChangeKind.Replaced or ExtensionRegistryChangeKind.Restored)
                _runtime.Harness.RegisterSkill(change.SourceId, skill);
            else if (change.Kind == ExtensionRegistryChangeKind.Removed)
                _runtime.Harness.UnregisterSkill(change.SourceId, skill.Name);

            RefreshResourceBinding();
        }

        return Task.CompletedTask;
    }

    private static void PopulateResourceBinding(ExtensionRuntimeBinding binding, SessionRuntime runtime)
    {
        var items = new List<ExtensionResourceItem>();
        AddSkillPaths(items, runtime);

        var resources = runtime.Resources;
        if (resources is null)
        {
            FinalizeResourceBinding(binding, items);
            return;
        }

        AddExistingFilePaths(items, "prompt", resources.PromptTemplatePaths);
        AddExistingFilePaths(items, "theme", resources.ThemePaths);
        AddExistingFilePaths(items, "context", resources.ContextFilePaths);
        AddExistingFilePaths(items, "system-prompt", resources.SystemPromptPaths);
        AddPackagePaths(items, resources.Packages);

        FinalizeResourceBinding(binding, items);
    }

    private static void FinalizeResourceBinding(ExtensionRuntimeBinding binding, List<ExtensionResourceItem> items)
    {
        binding.ResourceItems = items.Distinct().ToList();

        var authorized = new HashSet<string>(binding.ResourceItems.Select(item => item.Path), StringComparer.Ordinal);
        binding.ReadResourceAsync = async (path, ct) =>
        {
            if (!authorized.Contains(path)) return null;
            try
            {
                var content = await File.ReadAllTextAsync(path, ct);
                return new ExtensionResourceContent(path, content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        };
    }

    internal void RefreshResourceBinding()
    {
        if (_runtime is null) return;
        PopulateResourceBinding(_runtime.ExtensionBinding, _runtime);
    }

    private static void AddSkillPaths(List<ExtensionResourceItem> items, SessionRuntime runtime)
    {
        foreach (var skill in runtime.Harness.Skills)
        {
            if (!string.IsNullOrEmpty(skill.FilePath) && File.Exists(skill.FilePath))
                items.Add(new ExtensionResourceItem("skill", skill.FilePath));
        }

        var resources = runtime.Resources;
        if (resources is null) return;
        foreach (var path in resources.SkillPaths)
        {
            if (File.Exists(path))
                items.Add(new ExtensionResourceItem("skill", path));
        }
    }

    private static void AddExistingFilePaths(List<ExtensionResourceItem> items, string kind, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
                items.Add(new ExtensionResourceItem(kind, path));
        }
    }

    private static void AddPackagePaths(List<ExtensionResourceItem> items, IReadOnlyList<PiResolvedPackage> packages)
    {
        foreach (var pkg in packages)
        {
            var packageJson = Path.Combine(pkg.RootPath, "package.json");
            if (File.Exists(packageJson))
                items.Add(new ExtensionResourceItem("package", packageJson));
        }
    }

    private async Task<object?> GetSessionSnapshotAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        var key = await BuildSessionSnapshotCacheKeyAsync(runtime, cancellationToken);
        if (_sessionSnapshotCache is { } cached && cached.Key == key) return cached.Snapshot;

        var snapshot = await BuildSessionSnapshotAsync(runtime, cancellationToken);
        _sessionSnapshotCache = new RuntimeSessionSnapshotCacheEntry(key, snapshot);
        return snapshot;
    }

    private static async Task<RuntimeSessionSnapshotCacheKey> BuildSessionSnapshotCacheKeyAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        var leafId = await runtime.Session.GetLeafIdAsync(cancellationToken);
        return new RuntimeSessionSnapshotCacheKey(
            runtime.Session.Metadata.Id,
            runtime.Session.Metadata.Path,
            leafId,
            runtime.CurrentModelSelection.Model,
            runtime.CurrentModelSelection.IsScoped,
            SnapshotListKey(runtime.CurrentModelSelection.ScopedModels.Select(model => $"{model.Provider}/{model.Id}")),
            SnapshotListKey(runtime.Harness.ActiveToolNames),
            SnapshotListKey(runtime.Harness.AllToolNames),
            runtime.Harness.ThinkingLevel);
    }

    private static string SnapshotListKey(IEnumerable<string> values) => string.Join('\u001f', values);

    private static async Task<object?> BuildSessionSnapshotAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        var entries = await runtime.Session.GetEntriesAsync(cancellationToken);
        var branch = await runtime.Session.GetBranchAsync(cancellationToken: cancellationToken);
        var leafId = await runtime.Session.GetLeafIdAsync(cancellationToken);
        var labels = entries.OfType<LabelEntry>().Where(entry => !string.IsNullOrWhiteSpace(entry.TargetId))
            .GroupBy(entry => entry.TargetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Label, StringComparer.Ordinal);
        var children = entries.GroupBy(entry => entry.ParentId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var leafEntry = entries.FirstOrDefault(entry => string.Equals(entry.Id, leafId, StringComparison.Ordinal));
        var sessionDir = string.IsNullOrWhiteSpace(runtime.Session.Metadata.Path) ? null : Path.GetDirectoryName(runtime.Session.Metadata.Path);
        return new
        {
            sessionId = runtime.Session.Metadata.Id,
            sessionFile = runtime.Session.Metadata.Path,
            cwd = runtime.Session.Metadata.Cwd,
            leafId,
            leafEntry,
            entries,
            branch,
            tree = new { entries, rootIds = entries.Where(entry => entry.ParentId is null).Select(entry => entry.Id).ToArray(), childrenByParentId = children },
            childrenByParentId = children,
            labels,
            header = new { runtime.Session.Metadata.Id, runtime.Session.Metadata.Path, runtime.Session.Metadata.Cwd, leafId, sessionName = await runtime.Session.GetSessionNameAsync(cancellationToken) },
            sessionDir,
            isPersisted = !string.IsNullOrWhiteSpace(runtime.Session.Metadata.Path) && File.Exists(runtime.Session.Metadata.Path),
            contextUsage = new { tokenCount = branch.Sum(EstimateEntryTokens), entryCount = branch.Count, contextWindow = runtime.Harness.Model.ContextWindow },
            sessionName = await runtime.Session.GetSessionNameAsync(cancellationToken),
            model = runtime.CurrentModelSelection.Model,
            modelRegistry = new
            {
                current = runtime.CurrentModelSelection.Model,
                scopedModels = runtime.CurrentModelSelection.ScopedModels,
                isScoped = runtime.CurrentModelSelection.IsScoped,
                providers = ModelRegistry.GetProviders(),
                models = ModelRegistry.GetAllModels().Select(model => model.Descriptor).ToArray(),
                providerConfigs = ModelRegistry.GetProviderConfigs()
            },
            flags = runtime.ExtensionBinding.FlagValues,
            activeTools = runtime.Harness.ActiveToolNames.ToArray(),
            allTools = runtime.Harness.AllToolNames.ToArray(),
            thinkingLevel = runtime.Harness.ThinkingLevel.ToString().ToLowerInvariant()
        };
    }

    private static SubagentSessionOptions ParseSubagentOptions(object? optionsObj)
    {
        if (optionsObj is SubagentSessionOptions options) return options;
        if (optionsObj is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            ModelDescriptor? model = null;
            if (element.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.Object)
                model = AgentJsonSerializer.Deserialize<ModelDescriptor>(modelElement.GetRawText());
            ThinkingLevel? thinkingLevel = null;
            if (element.TryGetProperty("thinkingLevel", out var tlElement) && tlElement.ValueKind == JsonValueKind.String
                && Enum.TryParse<ThinkingLevel>(tlElement.GetString(), true, out var tl))
                thinkingLevel = tl;
            var sessionName = element.TryGetProperty("sessionName", out var sn) && sn.ValueKind == JsonValueKind.String ? sn.GetString() : null;
            var parentSessionPath = element.TryGetProperty("parentSessionPath", out var psp) && psp.ValueKind == JsonValueKind.String ? psp.GetString() : null;
            return new SubagentSessionOptions(model, thinkingLevel, sessionName, parentSessionPath);
        }
        return new SubagentSessionOptions();
    }

    private static async Task<object?> BuildSubagentSnapshotAsync(SubagentSessionHandle handle, CancellationToken cancellationToken)
    {
        var sessionName = await handle.Session.GetSessionNameAsync(cancellationToken);
        var entries = await handle.Session.GetEntriesAsync(cancellationToken);
        var context = await handle.Session.BuildContextAsync(cancellationToken);
        return new
        {
            sessionId = handle.SessionId,
            sessionFile = handle.Session.Metadata.Path,
            cwd = handle.Session.Metadata.Cwd,
            entries,
            messages = context.Messages,
            sessionName,
            model = handle.Harness.Model,
            thinkingLevel = handle.Harness.ThinkingLevel.ToString().ToLowerInvariant()
        };
    }

    private static int EstimateEntryTokens(SessionTreeEntry entry) => Math.Max(1, System.Text.Json.JsonSerializer.Serialize(entry).Length / 4);

    private sealed record RuntimeSessionSnapshotCacheEntry(RuntimeSessionSnapshotCacheKey Key, object? Snapshot);

    private sealed record RuntimeSessionSnapshotCacheKey(
        string SessionId,
        string Path,
        string? LeafId,
        ModelDescriptor Model,
        bool IsScoped,
        string ScopedModels,
        string ActiveTools,
        string AllTools,
        ThinkingLevel ThinkingLevel);

    private IReadOnlyList<ExtensionCommandInfo> BuildCommandInfos(SessionRuntime runtime)
    {
        var commands = new List<ExtensionCommandInfo>();
        if (extensionManager is not null)
        {
            foreach (var command in extensionManager.Registry.Commands)
            {
                commands.Add(new ExtensionCommandInfo(
                    command.Value.Name,
                    command.Value.Description,
                    "extension",
                    new ExtensionCommandSourceInfo(command.SourceId, command.SourceId)));
            }
        }

        foreach (var template in runtime.PromptTemplates.Templates)
        {
            commands.Add(new ExtensionCommandInfo(
                $"prompt:{template.Name}",
                template.Description ?? $"Run prompt template '{template.Name}'.",
                "prompt",
                SourceInfoForPath(template.SourcePath, "prompt", runtime)));
        }

        foreach (var skill in runtime.Harness.Skills)
        {
            commands.Add(new ExtensionCommandInfo(
                $"skill:{skill.Name}",
                skill.Description,
                "skill",
                SourceInfoForPath(skill.FilePath, "skill", runtime)));
        }

        return ApplyInvocationSuffixes(commands);
    }

    private static ExtensionCommandSourceInfo SourceInfoForPath(string path, string source, SessionRuntime runtime)
        => new(path, source, ScopeForPath(path, runtime.ExtensionBinding.Cwd), "top-level", BaseDirForPath(path));

    private static string ScopeForPath(string path, string cwd)
    {
        var fullPath = Path.GetFullPath(path);
        var fullCwd = Path.GetFullPath(cwd);
        return fullPath.StartsWith(fullCwd, StringComparison.OrdinalIgnoreCase) ? "project" : "user";
    }

    private static string? BaseDirForPath(string path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path));

    private static IReadOnlyList<ExtensionCommandInfo> ApplyInvocationSuffixes(IReadOnlyList<ExtensionCommandInfo> commands)
    {
        var groups = commands
            .GroupBy(command => command.Name)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExtensionCommandInfo>();
        foreach (var command in commands)
        {
            var count = seen.GetValueOrDefault(command.Name) + 1;
            seen[command.Name] = count;
            result.Add(groups[command.Name] == 1 ? command : command with { Name = $"{command.Name}:{count}" });
        }
        return result;
    }

    private static ExtensionSkillDefinition ToExtensionSkillDefinition(Skill skill)
        => new(skill.Name, skill.Description, skill.Content, skill.FilePath, skill.DisableModelInvocation,
            Override: ExtensionOverridePolicy.Reject,
            skill.Globs, skill.AlwaysApply, skill.Hide, skill.Source, skill.SourcePriority, skill.Runner);

    private static Func<CancellationToken, Task<IPackageCommandRunner>> CreatePackageRunnerFactory(SessionRuntime runtime)
        => async ct =>
        {
            var store = new PiSettingsStore();
            var snapshot = runtime.SettingsSnapshot ?? await store.LoadAsync(runtime.ExtensionBinding.Cwd, cancellationToken: ct);
            var settingsService = new PiPackageSettingsService(store, snapshot);
            var packageRoot = Path.Combine(snapshot.Paths.GlobalAgentDirectory, "packages");
            var packageManager = new PiPackageManager(packageRoot, new SystemProcessRunner());
            var nativeInstaller = new NativeExtensionInstaller(snapshot.Paths.HomeDirectory, snapshot.Paths.Cwd);
            return new PiPackageCommandRunner(settingsService, packageManager, nativeInstaller);
        };

    private static ExtensionSessionReplacementResult ToExtensionSessionReplacementResult(SessionRuntime.RuntimeSessionChangeResult result)
        => new(result.Cancelled, result.Reason, result.Session?.Id, result.Session?.Path);

    private static async Task InvokeWithSessionAsync(ExtensionRuntimeBinding binding, SessionRuntime runtime, ExtensionSessionReplacementResult result, Func<ExtensionSessionReplacementResult, IExtensionReplacementSessionApi?, CancellationToken, Task>? withSession, CancellationToken token)
    {
        if (result.Cancelled) return;
        var replacement = binding.CreateReplacementSessionApi(
            result,
            (message, delivery, triggerTurn, ct) => runtime.SendExtensionMessageAsync(message, delivery, triggerTurn, ct),
            (content, delivery, ct) => runtime.SendExtensionMessageAsync(AgentMessages.User(content), delivery, triggerTurn: delivery == ExtensionMessageDelivery.NextTurn, ct));
        if (withSession is not null) await withSession(result, replacement, token);
        if (binding.WithSessionCallback is not null) await binding.WithSessionCallback(result, replacement, token);
    }

    private Task<AgentToolResult<object?>> ExecuteToolByNameAsync(string toolName, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!_toolsByName.TryGetValue(toolName, out var tool))
            return Task.FromResult(new AgentToolResult<object?>([new TextContent($"Tool '{toolName}' was not found.")], null));
        var callId = $"eval-loopback:{Interlocked.Increment(ref _evalLoopbackCallCounter)}";
        return tool.ExecuteAsync(callId, parameters, cancellationToken);
    }

    private void ReplaceSubagentEventSubscription(string sessionId, IDisposable subscription)
    private void ReplaceSubagentEventSubscription(string sessionId, IDisposable subscription)
    {
        lock (_subagentEventSubscriptions)
        {
            if (_subagentEventSubscriptions.Remove(sessionId, out var previous)) previous.Dispose();
            _subagentEventSubscriptions[sessionId] = subscription;
        }
    }

    private void RemoveSubagentEventSubscription(string sessionId)
    {
        lock (_subagentEventSubscriptions)
        {
            if (_subagentEventSubscriptions.Remove(sessionId, out var subscription)) subscription.Dispose();
        }
    }

    private static IReadOnlyList<ExtensionThemeInfo> LoadAllThemes(SessionRuntime runtime)
    {
        var paths = runtime.Resources?.ThemePaths;
        if (paths is null || paths.Count == 0) return [];
        var documents = TuiThemeDocument.LoadAllAsync(paths, CancellationToken.None).GetAwaiter().GetResult();
        return documents.Select(ToExtensionThemeInfo).OfType<ExtensionThemeInfo>().ToList();
    }

    private static ExtensionThemeInfo? ToExtensionThemeInfo(TuiThemeDocument? document)
    {
        if (document is null) return null;
        return new ExtensionThemeInfo(document.Name, ToExtensionThemeDocument(document));
    }

    private static ExtensionThemeDocument ToExtensionThemeDocument(TuiThemeDocument document)
        => new(
            document.Name,
            document.Tokens,
            ToExtensionColorScheme(document.Default),
            ToExtensionColorScheme(document.Dialog),
            ToExtensionColorScheme(document.Menu));

    private static ExtensionThemeColorScheme? ToExtensionColorScheme(TuiColorSchemeDocument? scheme)
        => scheme is null ? null : new ExtensionThemeColorScheme(
            scheme.NormalForeground, scheme.NormalBackground,
            scheme.FocusForeground, scheme.FocusBackground,
            scheme.HotNormalForeground, scheme.HotNormalBackground,
            scheme.HotFocusForeground, scheme.HotFocusBackground,
            scheme.DisabledForeground, scheme.DisabledBackground);
    private void OnRuntimeThemeChanged(object? sender, EventArgs args)
        => _runtime?.ExtensionBinding.RaiseThemeChanged();

    public void Dispose()
    {
        if (extensionManager is not null) extensionManager.Registry.Changed -= ApplyExtensionRegistryChangeAsync;
        if (_runtime is not null) _runtime.ThemeChanged -= OnRuntimeThemeChanged;
        DisposeSubagentEventSubscriptions();
        if (_subagentService is not null) _subagentService.DisposeAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        _packageService?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (extensionManager is not null) extensionManager.Registry.Changed -= ApplyExtensionRegistryChangeAsync;
        if (_runtime is not null) _runtime.ThemeChanged -= OnRuntimeThemeChanged;
        DisposeSubagentEventSubscriptions();
        if (_subagentService is not null) await _subagentService.DisposeAllAsync(CancellationToken.None);
        _packageService?.Dispose();
        if (_managedSkillLoadTask is not null) await _managedSkillLoadTask.ConfigureAwait(false);
    }

    private void DisposeSubagentEventSubscriptions()
    {
        lock (_subagentEventSubscriptions)
        {
            foreach (var subscription in _subagentEventSubscriptions.Values) subscription.Dispose();
            _subagentEventSubscriptions.Clear();
        }
    }
    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
