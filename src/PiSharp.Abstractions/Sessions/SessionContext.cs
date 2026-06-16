using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;

namespace PiSharp.Abstractions.Sessions;

public sealed record SessionContext(
    IReadOnlyList<AgentMessage> Messages,
    ThinkingLevel ThinkingLevel,
    string? Provider = null,
    string? ModelId = null);
