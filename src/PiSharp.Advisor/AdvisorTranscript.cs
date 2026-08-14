using PiSharp.Abstractions.Messages;

namespace PiSharp.Advisor;

/// <summary>
/// Bounded rolling transcript of the recent conversation fed to the advisor
/// model. Rebuilt from <c>context</c> (<see cref="Reset"/>) events and appended
/// to on each <c>turn_end</c> (<see cref="Append"/>). The buffer is trimmed so
/// at most <see cref="MaxTurns"/> assistant turns are retained — the advisor
/// only ever sees an affordable slice of the conversation.
/// </summary>
public sealed class AdvisorTranscript
{
    private readonly List<AgentMessage> _messages = [];
    private int _maxTurns;

    public AdvisorTranscript(int maxTurns) => _maxTurns = Math.Max(1, maxTurns);

    /// <summary>The maximum number of assistant turns retained.</summary>
    public int MaxTurns => _maxTurns;

    /// <summary>Current bounded transcript snapshot.</summary>
    public IReadOnlyList<AgentMessage> Messages => _messages;

    public void SetMaxTurns(int maxTurns) => _maxTurns = Math.Max(1, maxTurns);

    /// <summary>Replaces the transcript with the tail of <paramref name="messages"/>.</summary>
    public void Reset(IEnumerable<AgentMessage> messages)
    {
        _messages.Clear();
        Append(messages);
    }

    /// <summary>Appends messages and trims the oldest turns to the bound.</summary>
    public void Append(IEnumerable<AgentMessage> messages)
    {
        foreach (var message in messages) _messages.Add(message);
        Trim();
    }

    private void Trim()
    {
        while (CountAssistantTurns() > _maxTurns)
        {
            _messages.RemoveAt(0);
        }
    }

    private int CountAssistantTurns() => _messages.Count(m => m.Role == "assistant");
}
