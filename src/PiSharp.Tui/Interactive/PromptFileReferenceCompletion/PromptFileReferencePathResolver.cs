namespace PiSharp.Tui.Interactive;

internal sealed class PromptFileReferencePathResolver
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _workingDirectory;

    internal PromptFileReferencePathResolver(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    internal (string BaseDirectory, string Query, string DisplayBase) ResolveScopedQuery(string rawQuery)
    {
        var normalized = rawQuery.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex < 0) return (_workingDirectory, normalized, string.Empty);

        var displayBase = normalized[..(slashIndex + 1)];
        var query = normalized[(slashIndex + 1)..];
        var baseDirectory = Path.GetFullPath(displayBase, _workingDirectory);
        if (!IsUnderWorkingDirectory(baseDirectory)) return (baseDirectory, query, string.Empty);

        var canonicalDisplayBase = Path.GetRelativePath(_workingDirectory, baseDirectory).Replace('\\', '/');
        if (canonicalDisplayBase == ".") canonicalDisplayBase = string.Empty;
        else canonicalDisplayBase = canonicalDisplayBase.TrimEnd('/') + "/";
        return (baseDirectory, query, canonicalDisplayBase);
    }

    internal bool IsUnderWorkingDirectory(string path)
        => IsUnderDirectory(path, _workingDirectory);

    internal static bool IsUnderDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, fullDirectory, PathComparison)) return true;
        return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, PathComparison)
            || fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, PathComparison);
    }
}
