namespace PiSharp.Abstractions.Errors;

public enum ExecutionErrorCode
{
    Aborted,
    Timeout,
    ShellUnavailable,
    SpawnError,
    CallbackError,
    Unknown
}

/// <summary>
/// Backend-independent shell execution failure.
/// </summary>
public sealed class ExecutionError : Exception
{
    public ExecutionErrorCode Code { get; }

    public ExecutionError(ExecutionErrorCode code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }
}
