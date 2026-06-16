namespace PiSharp.Abstractions.Errors;

public enum BranchSummaryErrorCode
{
    Aborted,
    SummarizationFailed,
    InvalidSession
}

/// <summary>
/// Branch summarization subsystem failure.
/// </summary>
public sealed class BranchSummaryError : Exception
{
    public BranchSummaryErrorCode Code { get; }

    public BranchSummaryError(BranchSummaryErrorCode code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}
