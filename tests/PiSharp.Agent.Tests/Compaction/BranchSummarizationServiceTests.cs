using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Compaction;
using PiSharp.Agent.Sessions;
using Xunit;

namespace PiSharp.Agent.Tests.Compaction;

public sealed class BranchSummarizationServiceTests
{
    [Fact]
    public async Task NoEntriesReturnsEmptySummary()
    {
        var result = await BranchSummarizationService.GenerateSummaryAsync([], (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("done")));
        Assert.Equal("No content to summarize", result.Summary);
    }

    [Fact]
    public async Task CollectEntriesReturnsOldBranchAfterDeepestCommonAncestor()
    {
        var session = CreateSession();
        var root = await session.AppendMessageAsync(AgentMessages.User("root"));
        var oldAssistant = await session.AppendMessageAsync(AgentMessages.Assistant("old branch work"));
        await session.MoveToAsync(root);
        var targetAssistant = await session.AppendMessageAsync(AgentMessages.Assistant("target branch"));
        await session.MoveToAsync(oldAssistant);

        var entries = await BranchSummarizationService.CollectEntriesAsync(session, oldAssistant, targetAssistant);

        var only = Assert.Single(entries);
        Assert.Equal(oldAssistant, only.Id);
    }

    [Fact]
    public async Task GenerateSummaryUsesCompletionDelegateAndAddsPreamble()
    {
        string? prompt = null;
        var entries = new SessionTreeEntry[]
        {
            Entry("u", null, AgentMessages.User("branch details")),
            Entry("t", "u", AgentMessages.ToolResult("tc", "read", "tool output")),
            new BranchSummaryEntry { Id = "b", ParentId = "t", Timestamp = DateTimeOffset.UtcNow, FromId = "old", Summary = "nested summary" },
            new CompactionEntry { Id = "c", ParentId = "b", Timestamp = DateTimeOffset.UtcNow, Summary = "compact summary", FirstKeptEntryId = "u", TokensBefore = 20 }
        };

        var result = await BranchSummarizationService.GenerateSummaryAsync(entries, (_, context, _, _) =>
        {
            prompt = ((TextContent)((UserMessage)context.Messages[0]).Content[0]).Text;
            return Task.FromResult(AgentMessages.Assistant("summary body"));
        });

        Assert.StartsWith(BranchPrompts.Preamble, result.Summary);
        Assert.Contains("summary body", result.Summary);
        Assert.Contains("branch details", prompt);
        Assert.DoesNotContain("tool output", prompt);
    }

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "memory.jsonl")));

    private static MessageEntry Entry(string id, string? parentId, AgentMessage message)
        => new() { Id = id, ParentId = parentId, Timestamp = DateTimeOffset.UtcNow, Message = message };
}
