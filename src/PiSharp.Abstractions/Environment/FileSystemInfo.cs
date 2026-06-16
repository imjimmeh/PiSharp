namespace PiSharp.Abstractions.Environment;

/// <summary>
/// Metadata for one addressed filesystem object.
/// </summary>
public sealed record FileSystemInfo(
    string Name,
    string Path,
    FileKind Kind,
    long Size,
    DateTimeOffset ModifiedAt);
