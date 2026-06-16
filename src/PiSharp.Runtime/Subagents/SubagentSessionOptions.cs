using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;

namespace PiSharp.Runtime.Subagents;

public sealed record SubagentSessionOptions(
    ModelDescriptor? Model = null,
    ThinkingLevel? ThinkingLevel = null,
    string? SessionName = null,
    string? ParentSessionPath = null);
