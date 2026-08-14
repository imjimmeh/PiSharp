using System.Text.Json;
using PiSharp.Abstractions.Messages;

namespace PiSharp.Runtime.Subagents;

public sealed record SubagentPromptResult(
    string SessionId,
    AssistantMessage FinalMessage,
    IReadOnlyList<AgentMessage> Messages,
    /// <summary>Schema-validated structured result captured from the child's terminating <c>yield</c> call.</summary>
    JsonElement? StructuredResult = null);
