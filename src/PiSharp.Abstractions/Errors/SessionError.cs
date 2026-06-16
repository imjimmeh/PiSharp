namespace PiSharp.Abstractions.Errors;

public enum SessionErrorCode
{
    NotFound,
    InvalidSession,
    InvalidEntry,
    InvalidForkTarget,
    Storage,
    Unknown
}

/// <summary>
/// Session subsystem failure.
/// </summary>
public sealed class SessionError : Exception
{
    public SessionErrorCode Code { get; }

    public SessionError(SessionErrorCode code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}
