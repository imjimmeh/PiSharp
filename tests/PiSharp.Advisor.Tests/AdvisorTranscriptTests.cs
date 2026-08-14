using PiSharp.Abstractions.Messages;
using Xunit;

namespace PiSharp.Advisor.Tests;

public class AdvisorTranscriptTests
{
    [Fact]
    public void Append_bounds_to_max_turns_of_assistant_messages()
    {
        var transcript = new AdvisorTranscript(2);

        // 5 turns, each a user message then an assistant message.
        for (var i = 0; i < 5; i++)
        {
            transcript.Append([AgentMessages.User($"user {i}"), AgentMessages.Assistant($"assistant {i}")]);
        }

        Assert.Equal(2, transcript.Messages.Count(m => m.Role == "assistant"));
        // The retained tail keeps the newest assistant message.
        Assert.Equal("assistant 4", TranscriptText(transcript, "assistant", last: true));
    }

    [Fact]
    public void Reset_replaces_entire_transcript()
    {
        var transcript = new AdvisorTranscript(3);
        transcript.Append([AgentMessages.User("old"), AgentMessages.Assistant("a")]);
        Assert.Single(transcript.Messages.Where(m => m.Role == "assistant"));

        transcript.Reset([AgentMessages.User("new1"), AgentMessages.Assistant("b"), AgentMessages.User("new2")]);

        Assert.Single(transcript.Messages.Where(m => m.Role == "assistant"));
        Assert.Contains(transcript.Messages, m => m is UserMessage u && ContainsText(u, "new1"));
        Assert.DoesNotContain(transcript.Messages, m => m is UserMessage u && ContainsText(u, "old"));
    }

    private static string TranscriptText(AdvisorTranscript transcript, string role, bool last)
    {
        var matches = transcript.Messages.Where(m => m.Role == role).ToArray();
        var target = last ? matches[^1] : matches[0];
        return target is AssistantMessage a ? TextOf(a.Content) : string.Empty;
    }

    private static bool ContainsText(UserMessage m, string text) => TextOf(m.Content).Contains(text, StringComparison.Ordinal);

    private static string TextOf(IReadOnlyList<MessageContent> content)
        => string.Concat(content.OfType<TextContent>().Select(t => t.Text));
}
