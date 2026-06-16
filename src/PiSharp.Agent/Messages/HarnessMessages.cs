using PiSharp.Abstractions.Messages;

namespace PiSharp.Agent.Messages;

public static class HarnessMessageText
{
    public const string CompactionSummaryPrefix = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";
    public const string CompactionSummarySuffix = "\n</summary>";
    public const string BranchSummaryPrefix = "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n";
    public const string BranchSummarySuffix = "</summary>";
}

public sealed record BashExecutionMessage(
    string Command,
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    string? FullOutputPath = null,
    bool ExcludeFromContext = false,
    DateTimeOffset Timestamp = default)
    : CustomAgentMessage("bashExecution", Timestamp == default ? DateTimeOffset.UtcNow : Timestamp);

public sealed record CustomMessage(
    string CustomType,
    string? TextContent,
    IReadOnlyList<MessageContent>? ContentBlocks,
    bool Display,
    object? Details = null,
    DateTimeOffset Timestamp = default)
    : CustomAgentMessage("custom", Timestamp == default ? DateTimeOffset.UtcNow : Timestamp);

public sealed record BranchSummaryMessage(
    string Summary,
    string FromId,
    DateTimeOffset Timestamp = default)
    : CustomAgentMessage("branchSummary", Timestamp == default ? DateTimeOffset.UtcNow : Timestamp);

public sealed record CompactionSummaryMessage(
    string Summary,
    int TokensBefore,
    DateTimeOffset Timestamp = default)
    : CustomAgentMessage("compactionSummary", Timestamp == default ? DateTimeOffset.UtcNow : Timestamp);

public static class HarnessMessages
{
    public static string BashExecutionToText(BashExecutionMessage message)
    {
        var text = $"Ran `{message.Command}`\n";
        text += string.IsNullOrEmpty(message.Output) ? "(no output)" : $"```\n{message.Output}\n```";
        if (message.Cancelled)
        {
            text += "\n\n(command cancelled)";
        }
        else if (message.ExitCode is not null and not 0)
        {
            text += $"\n\nCommand exited with code {message.ExitCode}";
        }

        if (message.Truncated && !string.IsNullOrWhiteSpace(message.FullOutputPath))
        {
            text += $"\n\n[Output truncated. Full output: {message.FullOutputPath}]";
        }

        return text;
    }

    public static CustomMessage Custom(
        string customType,
        string content,
        bool display,
        object? details = null,
        DateTimeOffset timestamp = default)
        => new(customType, content, null, display, details, timestamp);

    public static CustomMessage Custom(
        string customType,
        IReadOnlyList<MessageContent> content,
        bool display,
        object? details = null,
        DateTimeOffset timestamp = default)
        => new(customType, null, content, display, details, timestamp);
}
