using System.Diagnostics;

namespace PiSharp.Git.Tests;

/// <summary>
/// Creates a real git repository in a temp directory for fixture-based tests. Uses the
/// git CLI on PATH (the same assumption the feature makes). Windows-safe: subprocess
/// with argument arrays, no shell.
/// </summary>
public sealed class GitFixture : IAsyncDisposable
{
    private static int _counter;

    public string RepoPath { get; }

    public string File(string relative) => Path.Combine(RepoPath, relative.Replace('/', Path.DirectorySeparatorChar));

    public GitFixture(bool createBaseCommit = true)
    {
        var unique = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}-{Interlocked.Increment(ref _counter)}";
        RepoPath = Path.Combine(Path.GetTempPath(), "pisharp-git-fixture-" + unique);
        Directory.CreateDirectory(RepoPath);
        Thread.Sleep(20);

        Run("init", "-q", "-b", "main");
        Run("config", "user.name", "Fixture User");
        Run("config", "user.email", "fixture@example.com");
        Run("config", "commit.gpgsign", "false");
        Run("config", "core.autocrlf", "false");

        if (createBaseCommit)
        {
            WriteFile("README.md", "# fixture\n");
            Run("add", "-A");
            Commit("initial");
        }
    }

    public void WriteFile(string relative, string content)
    {
        var path = File(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    public void DeleteFile(string relative) => System.IO.File.Delete(File(relative));

    public void MoveFile(string from, string to)
    {
        System.IO.File.Move(File(from), File(to));
    }

    public void Add(params string[] relative) => Run(["add", "-A", "--", .. relative]);

    public void Commit(string message) => Run(["commit", "-q", "-m", message]);

    public string Head() => Run("rev-parse", "HEAD").Trim();

    public string LogOneline() => Run("log", "--oneline", "-10").Trim();

    public string Status() => Run("status", "--porcelain=v1", "--untracked-files=all").Trim();

    public string CachedNameOnly() => Run("diff", "--cached", "--name-only").Trim();

    public string[] CommittedMessages()
        => Run("log", "--pretty=format:%s").Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public string Run(params string[] args) => RunImpl(args, null);

    private string RunImpl(IReadOnlyList<string> args, string? stdin)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true,
            WorkingDirectory = RepoPath
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(RepoPath))
            {
                Directory.Delete(RepoPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        return ValueTask.CompletedTask;
    }
}
