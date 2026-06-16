namespace PiSharp.Tui.Interactive;

internal interface IFileReferenceFileSystem
{
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    string GetFullPath(string path);
    string GetRelativePath(string relativeTo, string path);
}
