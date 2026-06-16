using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime.Session;
using Xunit;

namespace PiSharp.Runtime.Tests.Session;

public sealed class HtmlSessionRendererTests
{
    [Fact]
    public async Task HtmlRenderer_produces_valid_html_with_session_entries()
    {
        var session = CreateSessionWithEntries(
            AgentMessages.User("Hello"),
            AgentMessages.Assistant("Hi there"));

        var html = await HtmlSessionRenderer.RenderAsync(session, CancellationToken.None);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.EndsWith("</html>" + Environment.NewLine, html);
    }

    [Fact]
    public async Task HtmlRenderer_includes_entry_content()
    {
        var session = CreateSessionWithEntries(
            AgentMessages.User("What is the capital of France?"),
            AgentMessages.Assistant("The capital of France is Paris."));

        var html = await HtmlSessionRenderer.RenderAsync(session, CancellationToken.None);

        Assert.Contains("What is the capital of France?", html);
        Assert.Contains("The capital of France is Paris.", html);
    }

    [Fact]
    public async Task HtmlRenderer_encodes_special_characters()
    {
        var session = CreateSessionWithEntries(
            AgentMessages.User("<script>alert('xss')</script>"));

        var html = await HtmlSessionRenderer.RenderAsync(session, CancellationToken.None);

        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert('xss')</script>", html);
    }

    [Fact]
    public async Task HtmlRenderer_handles_empty_session()
    {
        var metadata = new JsonlSessionMetadata(
            "empty-session",
            DateTimeOffset.UtcNow,
            Environment.CurrentDirectory,
            Path.Combine(Path.GetTempPath(), "empty-session.jsonl"));

        var storage = new MemorySessionStorage<JsonlSessionMetadata>(metadata);
        var session = new PiSharp.Agent.Sessions.Session<JsonlSessionMetadata>(storage);

        var html = await HtmlSessionRenderer.RenderAsync(session, CancellationToken.None);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.EndsWith("</html>" + Environment.NewLine, html);
        Assert.DoesNotContain("<div class=\"entry ", html);
    }

    private static ISession<JsonlSessionMetadata> CreateSessionWithEntries(params AgentMessage[] messages)
    {
        var metadata = new JsonlSessionMetadata(
            "test-session",
            DateTimeOffset.UtcNow,
            Environment.CurrentDirectory,
            Path.Combine(Path.GetTempPath(), "test-session.jsonl"));

        var entries = new List<SessionTreeEntry>();
        string? parentId = null;

        foreach (var message in messages)
        {
            var id = Guid.NewGuid().ToString("N");
            entries.Add(new MessageEntry
            {
                Id = id,
                ParentId = parentId,
                Timestamp = DateTimeOffset.UtcNow,
                Message = message
            });
            parentId = id;
        }

        var storage = new MemorySessionStorage<JsonlSessionMetadata>(metadata, entries);
        return new PiSharp.Agent.Sessions.Session<JsonlSessionMetadata>(storage);
    }
}
