using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;

namespace PiSharp.Extensions;

public sealed record ExtensionResourceItem(string Kind, string Path);
public sealed record ExtensionResourceContent(string Path, string Content);

public sealed class ExtensionRuntimeBinding
{
    private readonly Dictionary<string, ExtensionFlagRegistration> _flagRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _flagValues = new(StringComparer.Ordinal);

    public ExtensionRuntimeBinding(string cwd, bool hasUi, IExtensionUi ui)
    {
        Cwd = cwd;
        HasUi = hasUi;
        Ui = ui;
        Session = new BindingSessionApi(this);
        Tools = new BindingToolApi(this);
        Skills = new BindingSkillApi(this);
        Model = new BindingModelApi(this);
    }

    public string Cwd { get; }
    public bool HasUi { get; private set; }
    public IExtensionUi Ui { get; private set; }
    public IExtensionSessionApi Session { get; }
    public IExtensionToolApi Tools { get; }
    public IExtensionSkillApi Skills { get; }
    public IExtensionModelApi Model { get; }

    public Func<AgentMessage, ExtensionMessageDelivery, bool, CancellationToken, Task> SendMessageAsync { get; set; } = (_, _, _, _) => Task.CompletedTask;
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
    public Func<IReadOnlyList<string>, CancellationToken, Task> SetActiveToolsAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<IReadOnlyList<ExtensionSkillRegistration>>> GetAllSkillsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<ExtensionSkillRegistration>>([]);
    public Func<CancellationToken, Task<IReadOnlyList<string>>> GetSelectedSkillsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<string>>([]);
    public Func<IReadOnlyList<string>, CancellationToken, Task> SetSelectedSkillsAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<IReadOnlyList<ExtensionCommandInfo>>> GetCommandsAsync { get; set; } = _ => Task.FromResult<IReadOnlyList<ExtensionCommandInfo>>([]);
    public Func<CancellationToken, Task> WaitForIdleAsync { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task<ExtensionSessionReplacementResult>> NewSessionAsync { get; set; } = _ => Task.FromResult(new ExtensionSessionReplacementResult(true, "Extension runtime is not bound."));
    public Func<string?, string?, CancellationToken, Task<ExtensionSessionReplacementResult>> ForkSessionAsync { get; set; } = (_, _, _) => Task.FromResult(new ExtensionSessionReplacementResult(true, "Extension runtime is not bound."));
    public Func<string, CancellationToken, Task> NavigateTreeAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, CancellationToken, Task<ExtensionSessionReplacementResult>> SwitchSessionAsync { get; set; } = (_, _) => Task.FromResult(new ExtensionSessionReplacementResult(true, "Extension runtime is not bound."));
    public Func<CancellationToken, Task<bool>> IsIdleAsync { get; set; } = _ => Task.FromResult(true);
    public Func<CancellationToken, Task<bool>> HasPendingMessagesAsync { get; set; } = _ => Task.FromResult(false);
    public Func<string?, CancellationToken, Task> CompactAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task<string>> GetSystemPromptAsync { get; set; } = _ => Task.FromResult(string.Empty);
    public Func<CancellationToken, Task> AbortAsync { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task> ShutdownAsync { get; set; } = _ => Task.CompletedTask;
    public Func<ModelDescriptor, CancellationToken, Task<bool>> SetModelAsync { get; set; } = (_, _) => Task.FromResult(false);
    public Func<CancellationToken, Task<ThinkingLevel?>> GetThinkingLevelAsync { get; set; } = _ => Task.FromResult<ThinkingLevel?>(null);
    public Func<ThinkingLevel, CancellationToken, Task> SetThinkingLevelAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task> ReloadExtensionsAsync { get; set; } = _ => Task.CompletedTask;
    public Func<string, object?, CancellationToken, Task> EmitEventAsync { get; set; } = (_, _, _) => Task.CompletedTask;
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
    }

    private sealed class BindingToolApi(ExtensionRuntimeBinding binding) : IExtensionToolApi
    {
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => new DisposableAction(() => { });
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default) => binding.GetActiveToolsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default) => binding.GetAllToolsAsync(cancellationToken);
        public Task SetActiveToolsAsync(IReadOnlyList<string> toolNames, CancellationToken cancellationToken = default) => binding.SetActiveToolsAsync(toolNames, cancellationToken);
    }

    private sealed class BindingSkillApi(ExtensionRuntimeBinding binding) : IExtensionSkillApi
    {
        public IDisposable RegisterSkill(ExtensionSkillRegistration registration) => new DisposableAction(() => { });
        public Task<IReadOnlyList<ExtensionSkillRegistration>> GetAllSkillsAsync(CancellationToken cancellationToken = default) => binding.GetAllSkillsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default) => binding.GetSelectedSkillsAsync(cancellationToken);
        public Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default) => binding.SetSelectedSkillsAsync(skillNames, cancellationToken);
    }

    private sealed class BindingModelApi(ExtensionRuntimeBinding binding) : IExtensionModelApi
    {
        public Task<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default) => binding.SetModelAsync(model, cancellationToken);
        public Task<ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default) => binding.GetThinkingLevelAsync(cancellationToken);
        public Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default) => binding.SetThinkingLevelAsync(level, cancellationToken);
    }
}
