using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Messages;

namespace PiSharp.Agent.Compaction;

public static class CompactionService
{
    public static readonly CompactionSettings Default = new(true, 16384, 20000);

    public static int EstimateTokens(AgentMessage message) => message switch
    {
        UserMessage user => ContentTokens(user.Content),
        AssistantMessage assistant => ContentTokens(assistant.Content) + assistant.Content.OfType<ToolCallContent>().Sum(c => c.Name.Length + c.Arguments.GetRawText().Length) / 4,
        ToolResultMessage tool => ContentTokens(tool.Content),
        BashExecutionMessage bash => (bash.Command.Length + bash.Output.Length + 3) / 4,
        BranchSummaryMessage branch => (branch.Summary.Length + 3) / 4,
        CompactionSummaryMessage compaction => (compaction.Summary.Length + 3) / 4,
        _ => 1
    };

    private static int ContentTokens(IReadOnlyList<MessageContent> content) => content.Sum(c => c switch { TextContent text => (text.Text.Length + 3) / 4, ThinkingContent think => (think.Thinking.Length + 3) / 4, ImageContent => 4800, _ => 10 });

    public static bool ShouldCompact(int contextTokens, int contextWindow, CompactionSettings settings) => settings.Enabled && contextTokens > contextWindow - settings.ReserveTokens;

    public static (int firstKeptIndex, bool isSplitTurn, int turnStartIndex) FindCutPoint(IReadOnlyList<SessionTreeEntry> entries, int keepRecentTokens)
    {
        var cutPoints = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is MessageEntry or BranchSummaryEntry or CustomMessageEntry) cutPoints.Add(i);
        }
        if (cutPoints.Count == 0) return (0, false, -1);
        var accumulated = 0;
        var cutIndex = cutPoints[0];
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is not MessageEntry m) continue;
            accumulated += EstimateTokens(m.Message);
            if (accumulated >= keepRecentTokens) { cutIndex = cutPoints.LastOrDefault(c => c >= i, cutPoints[0]); break; }
        }
        while (cutIndex > 0 && entries[cutIndex - 1] is CompactionEntry) cutIndex--;
        var isUser = entries[cutIndex] is MessageEntry { Message: UserMessage };
        return isUser ? (cutIndex, false, -1) : (cutIndex, true, FindTurnStart(entries, cutIndex));
    }

    private static int FindTurnStart(IReadOnlyList<SessionTreeEntry> entries, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (entries[i] is BranchSummaryEntry or CustomMessageEntry) return i;
            if (entries[i] is MessageEntry { Message: UserMessage }) return i;
        }
        return -1;
    }

    public static CompactionPreparation? Prepare(IReadOnlyList<SessionTreeEntry> path, CompactionSettings settings)
    {
        if (path.Count == 0 || path[^1] is CompactionEntry) return null;
        var prevCompaction = path.OfType<CompactionEntry>().LastOrDefault();
        var previousSummary = prevCompaction?.Summary;
        var boundary = 0;
        if (prevCompaction is not null)
        {
            var list = path.ToList();
            var idx = list.FindIndex(e => e.Id == prevCompaction.FirstKeptEntryId);
            boundary = idx >= 0 ? idx : list.FindIndex(e => e.Id == prevCompaction.Id) + 1;
        }
        var (cutIndex, isSplit, turnStart) = FindCutPoint(path, settings.KeepRecentTokens);
        var firstKeptId = path[Math.Max(cutIndex, boundary)].Id;
        var historyEnd = isSplit ? turnStart : cutIndex;
        var toSummarize = path.Skip(boundary).Take(historyEnd - boundary).Select(ToCompactionMessage).OfType<AgentMessage>().ToArray();
        var turnPrefix = isSplit ? path.Skip(turnStart).Take(cutIndex - turnStart).Select(ToCompactionMessage).OfType<AgentMessage>().ToArray() : [];
        var tokensBefore = path.SelectMany(e => e is MessageEntry m ? new AgentMessage[] { m.Message } : Array.Empty<AgentMessage>()).Select(EstimateTokens).Sum();
        var fileOps = new FileOperations(new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
        return new CompactionPreparation(firstKeptId, toSummarize, turnPrefix, isSplit, tokensBefore, previousSummary, fileOps, settings);
    }

    private static AgentMessage? ToCompactionMessage(SessionTreeEntry entry) => entry switch
    {
        MessageEntry m => m.Message,
        CustomMessageEntry c => CustomMessageContent.ToCustomMessage(c.CustomType, c.Content, c.Display, c.Details),
        BranchSummaryEntry b => new BranchSummaryMessage(b.Summary, b.FromId, b.Timestamp),
        _ => null
    };

    public static async Task<CompactResult> CompactAsync(CompactionPreparation prep, AgentCompletionAsync complete, CancellationToken cancellationToken = default)
    {
        var model = new PiSharp.Agent.Core.Models.ModelDescriptor("compaction", "compaction", "compaction");
        var context = new AgentContext(CompactionPrompts.SystemPrompt, prep.MessagesToSummarize, null);
        var prompt = BuildPrompt(prep);
        var response = await complete(model, new AgentContext(CompactionPrompts.SystemPrompt, [AgentMessages.User(prompt)], null), new AgentStreamOptions(), cancellationToken);
        return new CompactResult(response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "Summary unavailable.", prep.FirstKeptEntryId, prep.TokensBefore);
    }

    private static string BuildPrompt(CompactionPreparation prep)
    {
        var conversation = string.Join("\n", prep.MessagesToSummarize.Select(ConversationSerializer.SerializeMessage));
        var instructions = prep.PreviousSummary is null ? CompactionPrompts.Initial : CompactionPrompts.Update;
        var text = $"<conversation>\n{conversation}\n</conversation>\n\n{instructions}";
        return prep.PreviousSummary is null ? text : $"{text}\n\n<previous-summary>\n{prep.PreviousSummary}\n</previous-summary>";
    }
}

public static class CompactionPrompts
{
    public const string SystemPrompt = "You are a context summarization assistant. Output ONLY the structured summary.";
    public const string Initial = "Create a structured context checkpoint summary.\n\n## Goal\n...\n\n## Constraints & Preferences\n...\n\n## Progress\n### Done\n...\n\n### In Progress\n...\n\n## Key Decisions\n...\n\n## Next Steps\n...";
    public const string Update = "Update the existing summary with NEW messages from above. PRESERVE all existing information.\n\n## Goal\n...\n\n## Constraints & Preferences\n...\n\n## Progress\n### Done\n...\n\n### In Progress\n...\n\n## Key Decisions\n...\n\n## Next Steps\n...";
}
