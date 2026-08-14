using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Extensions;

public delegate Task ExtensionCommandArgsHandler(string args, CancellationToken cancellationToken);
public delegate Task ExtensionCommandContextHandler(ExtensionCommandContext context, CancellationToken cancellationToken);

public enum ExtensionMessageDelivery { Steer, FollowUp, NextTurn }
public enum ExtensionFlagType { Boolean, String }

public enum ExtensionOverridePolicy
{
    Reject = 0,
    Override = 1,
    OverrideBuiltIn = 2
}

public sealed record ExtensionCommandSourceInfo(
    string Path,
    string Source,
    string Scope = "temporary",
    string Origin = "top-level",
    string? BaseDir = null);

public sealed record ExtensionCommandInfo(
    string Name,
    string? Description,
    string Source,
    ExtensionCommandSourceInfo SourceInfo);

public sealed record ExtensionSessionReplacementResult(bool Cancelled, string? Reason = null, string? SessionId = null, string? SessionFile = null);

public sealed record ExtensionCommandContext(
    string Name,
    string Args,
    IExtensionUi Ui,
    IExtensionSessionApi Session,
    IExtensionModelApi Model,
    IExtensionToolApi Tools,
    IReadOnlyDictionary<string, object?> Flags,
    CancellationToken CancellationToken);

public interface IExtensionSessionApi
{
    Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
        => SendMessageAsync(message, ExtensionMessageDelivery.NextTurn, false, cancellationToken);
    Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default);
    Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default);
    Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default);
    Task<string?> GetNameAsync(CancellationToken cancellationToken = default);
    Task SetNameAsync(string name, CancellationToken cancellationToken = default);
    Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default);

    // --- Session control (GAP-54) ---

    /// <summary>
    /// Creates a fresh session, replacing the current one. Fires
    /// <see cref="ExtensionEventNames.SessionBeforeSwitch"/> then
    /// <see cref="ExtensionEventNames.SessionShutdown"/> for the old session.
    /// If <paramref name="withSession"/> is provided, it is invoked with a
    /// replacement session API bound to the new session after the replacement
    /// completes but before control returns to the caller.
    /// </summary>
    Task<ExtensionSessionReplacementResult> NewSessionAsync(
        Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ExtensionSessionReplacementResult(true, "Session control is not supported by this extension host."));

    /// <summary>
    /// Forks the current session at <paramref name="entryId"/> (or the leaf if
    /// null) with <paramref name="position"/> ("before" or "at"). The fork
    /// becomes the active session. <paramref name="withSession"/> receives the
    /// replacement context.
    /// </summary>
    Task<ExtensionSessionReplacementResult> ForkAsync(
        string? entryId = null,
        string? position = "before",
        Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ExtensionSessionReplacementResult(true, "Session control is not supported by this extension host."));

    /// <summary>
    /// Switches to an existing session by path or id. If the session is not
    /// found, returns <see cref="ExtensionSessionReplacementResult"/> with
    /// <c>Cancelled = true</c>. <paramref name="withSession"/> receives the
    /// replacement context.
    /// </summary>
    Task<ExtensionSessionReplacementResult> SwitchSessionAsync(
        string sessionPathOrId,
        Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ExtensionSessionReplacementResult(true, "Session control is not supported by this extension host."));

    /// <summary>
    /// Navigates the session branch tree to <paramref name="targetId"/>.
    /// If <paramref name="summarize"/> is true, generates a branch summary.
    /// Requires the harness to be idle; throws
    /// <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    Task NavigateTreeAsync(string targetId, bool summarize = false, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Waits until the agent harness is idle (no pending turn). Returns
    /// immediately if already idle.
    /// </summary>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Reports whether the agent harness is currently idle.
    /// </summary>
    Task<bool> IsIdleAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>
    /// Reports whether there are pending queued messages.
    /// </summary>
    Task<bool> HasPendingMessagesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

public interface IExtensionToolApi
{
    IDisposable RegisterTool(ExtensionToolRegistration registration);
    Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Restricts the active tool set to <paramref name="toolNames"/>; null or
    /// empty clears the restriction (all registered tools active). Mirrors
    /// <c>AgentHarness.SetActiveTools</c>.
    /// </summary>
    Task SetActiveToolsAsync(IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a tool by name, resolving the live <see cref="IAgentTool"/>
    /// from the harness/extension registry and calling its
    /// <see cref="IAgentTool.ExecuteAsync(string, System.Text.Json.JsonElement, CancellationToken, AgentToolUpdateCallback{object?}?)"/>
    /// directly (no harness event emission). Used by eval-kernel loopback
    /// bridges. Defaults to an error result when the host does not wire it.
    /// </summary>
    Task<AgentToolResult<object?>> ExecuteToolAsync(string toolName, JsonElement parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentToolResult<object?>(
            [new PiSharp.Abstractions.Messages.TextContent($"Tool '{toolName}' is not available in this extension host.")],
            null));
}

/// <summary>
/// Replacement-session context passed to <c>withSession</c> callbacks from
/// <see cref="IExtensionSessionApi.NewSessionAsync"/>,
/// <see cref="IExtensionSessionApi.ForkAsync"/>, and
/// <see cref="IExtensionSessionApi.SwitchSessionAsync"/>. Its
/// <see cref="SendMessageAsync"/> and <see cref="SendUserMessageAsync"/> are
/// bound to the replacement (new) session. After the callback returns, the
/// old session API and old pi session-bound actions are stale for session
/// work — use only the replacement context inside the callback.
/// </summary>
public interface IExtensionReplacementSessionApi
{
    /// <summary>Replacement-session-bound message send.</summary>
    Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.NextTurn, bool triggerTurn = false, CancellationToken cancellationToken = default);

    /// <summary>Replacement-session-bound user message send.</summary>
    Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default);

    /// <summary>The replacement session's id.</summary>
    string SessionId { get; }

    /// <summary>The replacement session's file path.</summary>
    string? SessionFile { get; }
}

public interface IExtensionSkillApi
{
    IDisposable RegisterSkill(ExtensionSkillDefinition registration);
    /// <summary>
    /// Registers a skill provider whose discovered skills merge with
    /// first-wins dedup by name (higher <c>SourcePriority</c> wins). Defaults
    /// to a throwing no-op so existing implementors and in-memory fakes are
    /// not forced to implement it; hosts that wire the runtime registry
    /// return a live facade here.
    /// </summary>
    IDisposable RegisterSkillProvider(ISkillProvider provider)
        => throw new NotSupportedException("This extension host does not support skill providers.");
    Task<IReadOnlyList<ExtensionSkillDefinition>> GetAllSkillsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default);
    Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default);
    /// <summary>
    /// Daemon-resident managed-skill store. Defaults to a throwing no-op so
    /// existing implementors and in-memory fakes are not forced to implement
    /// it; hosts that wire the runtime store return a live facade here.
    /// </summary>
    IExtensionManagedSkillApi ManagedSkills
        => throw new NotSupportedException("This extension host does not provide a managed-skill store.");
}

public interface IExtensionModelApi
{
    Task<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default);
    /// <summary>Returns the harness's current model, or null when unbound.</summary>
    Task<ModelDescriptor?> GetModelAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ModelDescriptor?>(null);
    Task<ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default);
    Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default);
    Task<ExtensionModelSelection?> ResolveRoleAsync(string role, CancellationToken cancellationToken = default)
        => Task.FromResult<ExtensionModelSelection?>(null);
    Task<bool> SetModelByRoleAsync(string role, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

public sealed record ExtensionModelSelection(ModelDescriptor Model, ThinkingLevel ThinkingLevel);

public interface IExtensionEventBus
{
    IDisposable On(string eventName, ExtensionEventHandler handler);
    Task EmitAsync(string eventName, object payload, CancellationToken cancellationToken = default);
}

public interface IExtensionPromptApi
{
    IDisposable RegisterContributor(IPromptContributor contributor);
    IDisposable RegisterSection(PromptSection section);
    IDisposable RegisterSection(ExtensionPromptSectionRegistration registration);
    IDisposable RegisterTransform(IPromptTransform transform);
}

public sealed record ExtensionPromptSectionRegistration(
    PromptSection Section,
    ExtensionOverridePolicy Override = ExtensionOverridePolicy.Reject);

public sealed record ExtensionCommandRegistration(string Name, string Description, ExtensionCommandArgsHandler Handler)
{
    public Task InvokeAsync(ExtensionCommandContext context, CancellationToken cancellationToken = default)
        => Handler(context.Args, cancellationToken);
}

public sealed record ExtensionShortcutRegistration(string Keys, string Description, ExtensionCommandArgsHandler Handler)
{
    public Task InvokeAsync(ExtensionCommandContext context, CancellationToken cancellationToken = default)
        => Handler(context.Args, cancellationToken);
}

public sealed record ExtensionFlagRegistration(string Name, string Description, ExtensionFlagType Type = ExtensionFlagType.Boolean, object? DefaultValue = null);
public sealed record ExtensionMessageRendererRegistration(
    string Name,
    ExtensionChatRowType RowType = ExtensionChatRowType.Unknown,
    ExtensionMessageRenderHandler? Handler = null,
    ExtensionOverridePolicy Override = ExtensionOverridePolicy.Reject,
    string? CustomType = null);
public sealed record ExtensionMessageDecoratorRegistration(
    string Name,
    ExtensionChatRowType RowType = ExtensionChatRowType.Unknown,
    ExtensionMessageDecorateHandler? Handler = null,
    int Order = 0,
    string? CustomType = null);
public sealed record ExtensionWidgetState(string Kind, string Content, string? Title = null, string Placement = "above-editor");
public sealed record ExtensionUiPlacementRecord(string ExtensionId, string Placement, string Kind, string Content, string? Title = null);

public sealed record ExtensionResourcesDiscoverPayload(string Cwd, string Reason);
public sealed record ExtensionResourcesDiscoverResult(
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptPaths,
    IReadOnlyList<string> ThemePaths);

public sealed record ExtensionUserBashPayload(string Command, bool ExcludeFromContext, string Cwd);
public sealed record ExtensionUserBashResult(ExtensionBashOperations? Operations = null, ExtensionBashResult? Result = null);
public sealed record ExtensionBashOperations(string? Command = null, string? Cwd = null, double? Timeout = null, bool? ExcludeFromContext = null);
public sealed record ExtensionBashResult(string Command, int ExitCode, string Output, string Error);
