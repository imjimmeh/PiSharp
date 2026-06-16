namespace PiSharp.Abstractions.Errors;

public enum FileErrorCode
{
    Aborted,
    NotFound,
    PermissionDenied,
    NotDirectory,
    IsDirectory,
    Invalid,
    NotSupported,
    Unknown
}

/// <summary>
/// Backend-independent filesystem failure.
/// </summary>
public sealed class FileError : Exception
{
    public FileErrorCode Code { get; }

    public string? Path { get; }

    public FileError(FileErrorCode code, string message, string? path = null, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
        Path = path;
    }
}
