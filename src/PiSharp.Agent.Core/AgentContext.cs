using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Agent.Core;

/// <summary>
/// Immutable context snapshot passed to a low-level agent loop.
/// </summary>
public sealed record AgentContext(
    string SystemPrompt,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<IAgentTool>? Tools = null);
