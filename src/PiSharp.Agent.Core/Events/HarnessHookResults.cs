using PiSharp.Abstractions.Messages;

namespace PiSharp.Agent.Core.Events;

public sealed record BeforeAgentStartResult(
    IReadOnlyList<AgentMessage>? Messages = null,
    string? SystemPrompt = null);

public sealed record ContextResult(
    IReadOnlyList<AgentMessage> Messages);

public sealed record BeforeProviderRequestResult(
    IReadOnlyDictionary<string, object?>? StreamOptionsPatch = null);

public sealed record BeforeProviderPayloadResult(
    object Payload);

public sealed record ToolCallResult(
    bool Block = false,
    string? Reason = null);

public sealed record ToolResultPatch(
    IReadOnlyList<MessageContent>? Content = null,
    object? Details = null,
    bool? IsError = null,
    bool? Terminate = null);

public sealed record SessionBeforeCompactResult(
    bool Cancel = false,
    object? Compaction = null);

public sealed record CompactResult(
    string Summary,
    string FirstKeptEntryId,
    int TokensBefore,
    object? Details = null);

public sealed record SessionBeforeTreeResult(
    bool Cancel = false,
    object? Summary = null,
    string? CustomInstructions = null,
    bool ReplaceInstructions = false,
    string? Label = null);

public sealed record NavigateTreeResult(
    bool Cancelled,
    string? EditorText = null,
    object? SummaryEntry = null);

public sealed record CompactionSettings(
    bool Enabled,
    int ReserveTokens,
    int KeepRecentTokens);

public sealed record FileOperations(
    IReadOnlySet<string> Read,
    IReadOnlySet<string> Written,
    IReadOnlySet<string> Edited);

public sealed record CompactionPreparation(
    string FirstKeptEntryId,
    IReadOnlyList<AgentMessage> MessagesToSummarize,
    IReadOnlyList<AgentMessage> TurnPrefixMessages,
    bool IsSplitTurn,
    int TokensBefore,
    string? PreviousSummary,
    FileOperations FileOps,
    CompactionSettings Settings);

public sealed record TreePreparation(
    string TargetId,
    string? OldLeafId);
