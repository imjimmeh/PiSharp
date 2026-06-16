using System.Diagnostics;

namespace PiSharp.Tui.Interactive.Components;

/// <summary>Queries and parses `git status --porcelain` for the Modified Files sidebar panel.</summary>
public static class ModifiedFilesProvider
{
    /// <summary>Runs git off the calling thread. Returns an empty list for non-git dirs or when git is unavailable.</summary>
    public static async Task<IReadOnlyList<string>> GetModifiedFilesAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null) return [];

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? Parse(output) : [];
        }
        catch
        {
            // Non-git directory, git not installed, or process failure: degrade to empty.
            return [];
        }
    }

    /// <summary>Parses porcelain v1 output into relative file paths (rename targets resolved).</summary>
    public static IReadOnlyList<string> Parse(string porcelainOutput)
    {
        if (string.IsNullOrWhiteSpace(porcelainOutput)) return [];

        var files = new List<string>();
        foreach (var rawLine in porcelainOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (rawLine.Length < 4) continue;
            var path = rawLine[3..].Trim();
            if (path.Length == 0) continue;

            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];

            files.Add(path.Trim('"'));
        }

        return files;
    }
}
