using PiSharp.Abstractions.Messages;

namespace PiSharp.Runtime.Subagents;

public sealed record SubagentPromptResult(
    string SessionId,
    AssistantMessage FinalMessage,
    IReadOnlyList<AgentMessage> Messages);
