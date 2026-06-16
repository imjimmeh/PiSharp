using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;

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
}

public interface IExtensionToolApi
{
    IDisposable RegisterTool(ExtensionToolRegistration registration);
    Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default);
    Task SetActiveToolsAsync(IReadOnlyList<string> toolNames, CancellationToken cancellationToken = default);
}

public interface IExtensionSkillApi
{
    IDisposable RegisterSkill(ExtensionSkillRegistration registration);
    Task<IReadOnlyList<ExtensionSkillRegistration>> GetAllSkillsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default);
    Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default);
}

public interface IExtensionModelApi
{
    Task<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default);
    Task<ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default);
    Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default);
}

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
