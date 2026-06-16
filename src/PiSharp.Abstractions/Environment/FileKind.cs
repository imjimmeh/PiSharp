namespace PiSharp.Abstractions.Environment;

/// <summary>
/// Backend-independent filesystem object kind. Symlinks are not followed automatically.
/// </summary>
public enum FileKind
{
    File,
    Directory,
    Symlink
}
