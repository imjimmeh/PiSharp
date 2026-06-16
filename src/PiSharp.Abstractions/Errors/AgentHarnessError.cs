namespace PiSharp.Abstractions.Errors;

public enum AgentHarnessErrorCode
{
    Busy,
    InvalidState,
    InvalidArgument,
    Session,
    Hook,
    Auth,
    Compaction,
    BranchSummary,
    Unknown
}

/// <summary>
/// Agent harness lifecycle or integration failure.
/// </summary>
public sealed class AgentHarnessError : Exception
{
    public AgentHarnessErrorCode Code { get; }

    public AgentHarnessError(AgentHarnessErrorCode code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}
