using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Compaction;
using PiSharp.Agent.Core.Events;
using Xunit;

namespace PiSharp.Agent.Tests.Compaction;

public sealed class CompactionServiceTests
{
    [Fact]
    public void EstimateTokensForTextIsCharDiv4()
    {
        var tokens = CompactionService.EstimateTokens(AgentMessages.User(new string('x', 40)));
        Assert.Equal(10, tokens);
    }

    [Fact]
    public void EstimateTokensCountsImagesConservatively()
    {
        var tokens = CompactionService.EstimateTokens(new UserMessage([new ImageContent("image/png", "base64")]));
        Assert.Equal(4800, tokens);
    }

    [Fact]
    public void FindCutPointReturnsUserBoundary()
    {
        var entries = new SessionTreeEntry[]
        {
            Entry("1", null, AgentMessages.User("first")),
            Entry("2", "1", AgentMessages.Assistant("response"))
        };
        var (idx, split, _) = CompactionService.FindCutPoint(entries, 10000);
        Assert.False(split);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void FindCutPointMarksSplitTurnWhenBoundaryWouldStartAtAssistant()
    {
        var entries = new SessionTreeEntry[]
        {
            Entry("1", null, AgentMessages.User("first")),
            Entry("2", "1", AgentMessages.Assistant(new string('a', 200)))
        };

        var (idx, split, turnStart) = CompactionService.FindCutPoint(entries, keepRecentTokens: 1);

        Assert.True(split);
        Assert.Equal(1, idx);
        Assert.Equal(0, turnStart);
    }

    [Fact]
    public void PrepareReturnsNullForEmptyOrCompactionTail()
    {
        Assert.Null(CompactionService.Prepare([], new CompactionSettings(true, 10, 10)));
        Assert.Null(CompactionService.Prepare([
            new CompactionEntry { Id = "c", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Summary = "done", FirstKeptEntryId = "k", TokensBefore = 10 }
        ], new CompactionSettings(true, 10, 10)));
    }

    [Fact]
    public void PrepareCarriesPreviousSummaryFromLatestCompaction()
    {
        var path = new SessionTreeEntry[]
        {
            Entry("old", null, AgentMessages.User("old message")),
            new CompactionEntry { Id = "c", ParentId = "old", Timestamp = DateTimeOffset.UtcNow, Summary = "previous summary", FirstKeptEntryId = "keep", TokensBefore = 100 },
            Entry("keep", "c", AgentMessages.User("kept message")),
            Entry("tail", "keep", AgentMessages.Assistant(new string('x', 100)))
        };

        var prep = CompactionService.Prepare(path, new CompactionSettings(true, ReserveTokens: 1, KeepRecentTokens: 1));

        Assert.NotNull(prep);
        Assert.Equal("previous summary", prep.PreviousSummary);
        Assert.Equal("tail", prep.FirstKeptEntryId);
        Assert.True(prep.TokensBefore > 0);
    }

    [Fact]
    public async Task CompactAsyncPassesPreviousSummaryToCompletionPrompt()
    {
        string? prompt = null;
        var prep = new CompactionPreparation(
            FirstKeptEntryId: "keep",
            MessagesToSummarize: [AgentMessages.User("new work")],
            TurnPrefixMessages: [],
            IsSplitTurn: false,
            TokensBefore: 10,
            PreviousSummary: "old summary",
            FileOps: new FileOperations(new HashSet<string>(), new HashSet<string>(), new HashSet<string>()),
            Settings: new CompactionSettings(true, 1, 1));

        var result = await CompactionService.CompactAsync(prep, (_, context, _, _) =>
        {
            prompt = ((TextContent)((UserMessage)context.Messages[0]).Content[0]).Text;
            return Task.FromResult(AgentMessages.Assistant("updated summary"));
        });

        Assert.Equal("updated summary", result.Summary);
        Assert.Equal("keep", result.FirstKeptEntryId);
        Assert.Contains("old summary", prompt);
        Assert.Contains("new work", prompt);
    }

    private static MessageEntry Entry(string id, string? parentId, AgentMessage message)
        => new() { Id = id, ParentId = parentId, Timestamp = DateTimeOffset.UtcNow, Message = message };
}
