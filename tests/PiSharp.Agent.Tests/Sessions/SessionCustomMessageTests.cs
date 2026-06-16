using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Messages;
using PiSharp.Agent.Sessions;
using Xunit;

namespace PiSharp.Agent.Tests.Sessions;

public sealed class SessionCustomMessageTests
{
    [Fact]
    public async Task BuildSessionContextPreservesStringCustomMessageContent()
    {
        var session = CreateSession();
        await session.AppendCustomMessageEntryAsync("approval-card", "Approve?", display: true);

        var context = await session.BuildContextAsync();
        var message = Assert.IsType<CustomMessage>(Assert.Single(context.Messages));

        Assert.Equal("approval-card", message.CustomType);
        Assert.Equal("Approve?", message.TextContent);
        Assert.Null(message.ContentBlocks);
        Assert.True(message.Display);
    }

    [Fact]
    public async Task BuildSessionContextPreservesJsonElementContentBlocks()
    {
        var content = JsonDocument.Parse("""
            [
              { "type": "text", "text": "Approve?" },
              { "type": "text", "text": "Second line" }
            ]
            """).RootElement.Clone();

        var session = CreateSession();
        await session.AppendCustomMessageEntryAsync("approval-card", content, display: true);

        var context = await session.BuildContextAsync();
        var message = Assert.IsType<CustomMessage>(Assert.Single(context.Messages));

        Assert.Equal("approval-card", message.CustomType);
        Assert.Null(message.TextContent);
        Assert.NotNull(message.ContentBlocks);
        Assert.Equal(2, message.ContentBlocks.Count);
        Assert.Equal("Approve?", Assert.IsType<TextContent>(message.ContentBlocks[0]).Text);
        Assert.Equal("Second line", Assert.IsType<TextContent>(message.ContentBlocks[1]).Text);
        Assert.True(message.Display);
    }

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, Environment.CurrentDirectory, "memory.jsonl")));
}
