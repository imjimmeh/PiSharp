using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Agent;

public sealed record AgentOptions(
    AgentLoopConfig LoopConfig,
    AgentStateSnapshot? InitialState = null,
    QueueMode SteeringMode = QueueMode.OneAtATime,
    QueueMode FollowUpMode = QueueMode.OneAtATime);

public sealed record AgentStateSnapshot(
    string SystemPrompt,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<IAgentTool> Tools,
    ThinkingLevel ThinkingLevel);

public interface IAgentState
{
    string SystemPrompt { get; set; }
    IReadOnlyList<AgentMessage> Messages { get; }
    IReadOnlyList<IAgentTool> Tools { get; }
    bool IsStreaming { get; }
    AgentMessage? StreamingMessage { get; }
    IReadOnlySet<string> PendingToolCalls { get; }
    string? ErrorMessage { get; }
}

internal sealed class MutableAgentState : IAgentState
{
    public string SystemPrompt { get; set; } = string.Empty;
    public List<AgentMessage> Messages { get; private set; } = [];
    public List<IAgentTool> Tools { get; private set; } = [];
    IReadOnlyList<AgentMessage> IAgentState.Messages => Messages;
    IReadOnlyList<IAgentTool> IAgentState.Tools => Tools;
    public bool IsStreaming { get; set; }
    public AgentMessage? StreamingMessage { get; set; }
    public IReadOnlySet<string> PendingToolCalls { get; set; } = new HashSet<string>();
    public string? ErrorMessage { get; set; }

    public static MutableAgentState Create(AgentStateSnapshot? snapshot)
        => new()
        {
            SystemPrompt = snapshot?.SystemPrompt ?? string.Empty,
            Messages = snapshot?.Messages.ToList() ?? [],
            Tools = snapshot?.Tools.ToList() ?? []
        };
}

internal sealed class PendingMessageQueue(QueueMode mode)
{
    private readonly Queue<AgentMessage> _messages = new();
    private readonly object _gate = new();
    public QueueMode Mode { get; set; } = mode;
    public void Enqueue(AgentMessage message) { lock (_gate) _messages.Enqueue(message); }
    public bool HasItems { get { lock (_gate) return _messages.Count > 0; } }

    public IReadOnlyList<AgentMessage> Drain()
    {
        lock (_gate)
        {
            if (_messages.Count == 0) return [];
            if (Mode == QueueMode.OneAtATime) return [_messages.Dequeue()];
            var drained = _messages.ToArray();
            _messages.Clear();
            return drained;
        }
    }

    public void Clear() { lock (_gate) _messages.Clear(); }
}

internal sealed record ActiveRun(CancellationTokenSource AbortController, Task Completion);

internal sealed class Subscription(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}
