using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Messages;

namespace PiSharp.Agent.Compaction;

public static class BranchSummarizationService
{
    public static async Task<IReadOnlyList<SessionTreeEntry>> CollectEntriesAsync<TMetadata>(ISession<TMetadata> session, string? oldLeafId, string targetId, CancellationToken cancellationToken = default) where TMetadata : ISessionMetadata
    {
        if (oldLeafId is null) return [];
        var oldPathIds = (await session.GetBranchAsync(oldLeafId, cancellationToken)).Select(e => e.Id).ToHashSet();
        var targetPath = await session.GetBranchAsync(targetId, cancellationToken);
        var commonAncestorId = targetPath.Select(e => e.Id).FirstOrDefault(oldPathIds.Contains);
        var entries = new List<SessionTreeEntry>();
        var current = oldLeafId;
        while (current is not null && current != commonAncestorId)
        {
            var entry = await session.GetEntryAsync(current, cancellationToken);
            if (entry is null) break;
            entries.Insert(0, entry);
            current = entry.ParentId;
        }
        return entries;
    }

    public static async Task<BranchSummaryResult> GenerateSummaryAsync(IReadOnlyList<SessionTreeEntry> entries, AgentCompletionAsync complete, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return new BranchSummaryResult("No content to summarize", [], []);
        var messages = entries.Select(ToBranchMessage).OfType<AgentMessage>().ToArray();
        var conversation = string.Join("\n", messages.Select(ConversationSerializer.SerializeMessage));
        var prompt = $"<conversation>\n{conversation}\n</conversation>\n\n{BranchPrompts.Prompt}";
        var model = new PiSharp.Agent.Core.Models.ModelDescriptor("branch", "branch", "branch");
        var response = await complete(model, new AgentContext(BranchPrompts.SystemPrompt, [AgentMessages.User(prompt)], null), new AgentStreamOptions(), cancellationToken);
        var summary = BranchPrompts.Preamble + (response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "No summary generated.");
        return new BranchSummaryResult(summary, [], []);
    }

    private static AgentMessage? ToBranchMessage(SessionTreeEntry entry) => entry switch
    {
        MessageEntry m when m.Message.Role is not "toolResult" => m.Message,
        CustomMessageEntry c => CustomMessageContent.ToCustomMessage(c.CustomType, c.Content, c.Display, c.Details),
        BranchSummaryEntry b => new BranchSummaryMessage(b.Summary, b.FromId, b.Timestamp),
        CompactionEntry c => new CompactionSummaryMessage(c.Summary, c.TokensBefore, c.Timestamp),
        _ => null
    };
}

public sealed record BranchSummaryResult(string Summary, IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);

public static class BranchPrompts
{
    public const string SystemPrompt = "You are a context summarization assistant.";
    public const string Preamble = "The user explored a different conversation branch before returning here.\nSummary of that exploration:\n\n";
    public const string Prompt = "Create a structured summary of this conversation branch.\n\n## Goal\n...\n\n## Progress\n### Done\n...\n\n### In Progress\n...\n\n## Next Steps\n...";
}
