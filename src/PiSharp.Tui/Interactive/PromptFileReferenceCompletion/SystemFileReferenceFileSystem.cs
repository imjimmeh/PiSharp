namespace PiSharp.Tui.Interactive;

internal sealed class SystemFileReferenceFileSystem : IFileReferenceFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);
}
