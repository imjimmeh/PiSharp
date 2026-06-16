using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using Xunit;

namespace PiSharp.Agent.Tests.Sessions;

public sealed class SessionTests
{
    [Fact]
    public async Task AppendsMessagesAndBuildsContextInOrder()
    {
        var session = CreateSession();
        await session.AppendMessageAsync(AgentMessages.User("hello"));
        await session.AppendMessageAsync(AgentMessages.Assistant("hi"));
        var context = await session.BuildContextAsync();
        Assert.Equal(2, context.Messages.Count);
    }

    [Fact]
    public async Task MoveToPersistsLeafAndAllowsBranching()
    {
        var session = CreateSession();
        var first = await session.AppendMessageAsync(AgentMessages.User("first"));
        await session.AppendMessageAsync(AgentMessages.Assistant("answer"));
        await session.MoveToAsync(first);
        await session.AppendMessageAsync(AgentMessages.User("branch"));
        Assert.Equal(2, (await session.GetBranchAsync()).Count);
    }

    [Fact]
    public async Task AppendMessageRejectsToolResultWithoutMatchingAssistantToolCall()
    {
        var session = CreateSession();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.AppendMessageAsync(AgentMessages.ToolResult("call-orphan", "read", "orphan result")));

        Assert.Contains("call-orphan", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await session.GetEntriesAsync());
    }

    [Fact]
    public async Task AppendMessageAllowsToolResultAfterMatchingAssistantToolCall()
    {
        var session = CreateSession();
        using var args = JsonDocument.Parse("{}");
        await session.AppendMessageAsync(new AssistantMessage([new ToolCallContent("call-1", "read", args.RootElement.Clone())]));

        await session.AppendMessageAsync(AgentMessages.ToolResult("call-1", "read", "read result"));

        var messages = (await session.BuildContextAsync()).Messages;
        Assert.Collection(messages,
            message => Assert.IsType<AssistantMessage>(message),
            message => Assert.IsType<ToolResultMessage>(message));
    }

    [Fact]
    public async Task AppendEntriesAsyncAppendsEntriesInOrderWithGeneratedParentChain()
    {
        var session = CreateSession();

        var ids = await session.AppendEntriesAsync([
            new ModelChangeEntry { Provider = "openai", ModelId = "gpt-4o", Id = string.Empty, ParentId = null, Timestamp = default },
            new MessageEntry { Message = AgentMessages.User("hello"), Id = string.Empty, ParentId = null, Timestamp = default }
        ]);

        Assert.Equal(2, ids.Count);
        var entries = await session.GetEntriesAsync();
        Assert.Collection(entries,
            entry =>
            {
                Assert.IsType<ModelChangeEntry>(entry);
                Assert.Equal(ids[0], entry.Id);
                Assert.Null(entry.ParentId);
            },
            entry =>
            {
                Assert.IsType<MessageEntry>(entry);
                Assert.Equal(ids[1], entry.Id);
                Assert.Equal(ids[0], entry.ParentId);
            });
        Assert.Equal(ids[1], await session.GetLeafIdAsync());
    }

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, Environment.CurrentDirectory, "memory.jsonl")));
}
