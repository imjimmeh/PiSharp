using System.ComponentModel;
using System.Diagnostics;

namespace PiSharp.Git;

/// <summary>Result of one git subprocess invocation.</summary>
public sealed record GitCommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Subprocess git wrapper. Arguments are passed as an argument array (never a command
/// string) so no shell quoting/injection hazards exist; NUL-delimited (-z) output is
/// read raw without quote unescaping.
/// </summary>
public interface IGitRunner
{
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        string? stdin = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when git itself cannot be started (e.g. not on PATH).</summary>
public sealed class GitException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class GitRunner : IGitRunner
{
    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        string? stdin = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new GitException("Failed to start git.");
        }
        catch (Win32Exception ex)
        {
            throw new GitException(
                "git was not found on PATH. The git integrations extension requires the git CLI to be installed and reachable.",
                ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        return new GitCommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
