using Microsoft.Extensions.Logging;

namespace PiSharp.Tui.Interactive;

internal readonly record struct FileReferenceEntry(string FullPath, string DisplayPath, bool IsDirectory);

internal sealed class FileReferenceEntryEnumerator
{
    internal const int MaxVisitedEntries = 5000;

    private readonly IFileReferenceFileSystem _fileSystem;
    private readonly ILogger _logger;

    internal FileReferenceEntryEnumerator(IFileReferenceFileSystem fileSystem, ILogger logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    internal IEnumerable<FileReferenceEntry> EnumerateEntries(string root, string displayBase, bool recursive)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited < MaxVisitedEntries)
        {
            var dir = stack.Pop();
            string[] entries;
            try
            {
                entries = _fileSystem.EnumerateFileSystemEntries(dir)
                    .Where(path => !IsGitPath(path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "File listing for directory {Dir} failed", dir);
                continue;
            }

            foreach (var entry in entries)
            {
                if (visited++ >= MaxVisitedEntries) yield break;
                var isDirectory = _fileSystem.DirectoryExists(entry);
                var relative = _fileSystem.GetRelativePath(root, entry).Replace('\\', '/');
                if (relative == ".") continue;
                var display = string.IsNullOrEmpty(displayBase)
                    ? relative
                    : displayBase.Replace('\\', '/') + relative;
                yield return new FileReferenceEntry(entry, display, isDirectory);
                if (recursive && isDirectory) stack.Push(entry);
            }
        }
    }

    internal FileReferenceEntry? CreateEntry(string fullPath, string root, string displayBase)
    {
        var isDirectory = _fileSystem.DirectoryExists(fullPath);
        var relative = _fileSystem.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative == ".") return null;
        var display = string.IsNullOrEmpty(displayBase)
            ? relative
            : displayBase.Replace('\\', '/') + relative;
        return new FileReferenceEntry(fullPath, display, isDirectory);
    }

    internal static bool IsGitPath(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Any(part => string.Equals(part, ".git", StringComparison.Ordinal));
}
