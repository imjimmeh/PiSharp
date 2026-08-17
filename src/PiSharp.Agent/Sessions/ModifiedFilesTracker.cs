using System.Diagnostics;

namespace PiSharp.Agent.Sessions;

public static class ModifiedFilesTracker
{
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
            return [];
        }
    }

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
