using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Tui.Interactive;

internal sealed class GitVisibilityService : IGitVisibilityService
{
    private readonly string _gitRoot;
    private readonly string _workingDirectory;
    private readonly ILogger _logger;
    private IReadOnlyList<GitIndexEntry>? _visibleEntries;

    private GitVisibilityService(string gitRoot, string workingDirectory, ILogger logger)
    {
        _gitRoot = gitRoot;
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    internal static GitVisibilityService? TryCreate(string workingDirectory, ILogger logger)
    {
        var current = Path.GetFullPath(workingDirectory);
        while (!string.IsNullOrEmpty(current))
        {
            var gitPath = Path.Combine(current, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return new GitVisibilityService(current, workingDirectory, logger);

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                return null;
            current = parent;
        }

        return null;
    }

    public IEnumerable<string> EnumerateVisiblePaths(string baseDirectory, bool recursive)
    {
        var fullBaseDirectory = Path.GetFullPath(baseDirectory);
        foreach (var entry in VisibleEntries())
        {
            if (!PromptFileReferencePathResolver.IsUnderDirectory(entry.FullPath, fullBaseDirectory)) continue;
            var relative = Path.GetRelativePath(fullBaseDirectory, entry.FullPath).Replace('\\', '/');
            if (relative == "." || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) continue;
            if (!recursive && relative.Contains('/', StringComparison.Ordinal)) continue;
            if (!PromptFileReferencePathResolver.IsUnderDirectory(entry.FullPath, _workingDirectory)) continue;
            yield return entry.FullPath;
        }
    }

    private IReadOnlyList<GitIndexEntry> VisibleEntries()
        => _visibleEntries ??= LoadVisibleEntries();

    private IReadOnlyList<GitIndexEntry> LoadVisibleEntries()
    {
        var entries = new Dictionary<string, GitIndexEntry>(PromptFileReferencePathResolver.PathComparer);
        foreach (var relativePath in ReadGitVisibleFiles())
        {
            var fullPath = Path.GetFullPath(Path.Combine(_gitRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath)) continue;

            entries[fullPath] = new GitIndexEntry(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(directory)
                && !string.Equals(directory, _gitRoot, PromptFileReferencePathResolver.PathComparison)
                && PromptFileReferencePathResolver.IsUnderDirectory(directory, _gitRoot))
            {
                entries[directory] = new GitIndexEntry(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        return entries.Values.OrderBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<string> ReadGitVisibleFiles()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-C");
            process.StartInfo.ArgumentList.Add(_gitRoot);
            process.StartInfo.ArgumentList.Add("ls-files");
            process.StartInfo.ArgumentList.Add("--cached");
            process.StartInfo.ArgumentList.Add("--others");
            process.StartInfo.ArgumentList.Add("--exclude-standard");
            process.StartInfo.ArgumentList.Add("-z");

            if (!process.Start()) return [];
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                return [];
            }

            if (process.ExitCode != 0) return [];
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace('\\', '/'))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "File listing failed");
            return [];
        }
    }

    private sealed record GitIndexEntry(string FullPath);
}
