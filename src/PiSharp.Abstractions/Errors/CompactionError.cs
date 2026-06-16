namespace PiSharp.Abstractions.Errors;

public enum CompactionErrorCode
{
    Aborted,
    SummarizationFailed,
    InvalidSession,
    Unknown
}

/// <summary>
/// Compaction subsystem failure.
/// </summary>
public sealed class CompactionError : Exception
{
    public CompactionErrorCode Code { get; }

    public CompactionError(CompactionErrorCode code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}
