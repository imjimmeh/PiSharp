using Xunit;

namespace PiSharp.Git.Tests;

public sealed class GitRunnerTests
{
    [Fact]
    public async Task RunsArgumentsWithoutShell()
    {
        var result = await new GitRunner().RunAsync(".", ["--version"], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonZeroExitCapturesStderr()
    {
        var result = await new GitRunner().RunAsync(".", ["this-command-does-not-exist"], null, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.Stderr));
    }

    [Fact]
    public async Task StdinIsDeliveredToProcess()
    {
        var result = await new GitRunner().RunAsync(".", ["hash-object", "--stdin"], "hello stdin", CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Matches("^[0-9a-f]{40}$", result.Stdout.Trim());
    }

    [Fact]
    public async Task GitNotOnPathThrowsGitException()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var filtered = string.Join(Path.PathSeparator,
            path.Split(Path.PathSeparator).Where(p => !p.Contains("Git", StringComparison.OrdinalIgnoreCase)));
        var previous = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", filtered);
        try
        {
            await Assert.ThrowsAsync<GitException>(() =>
                new GitRunner().RunAsync(".", ["--version"], null, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }

    [Fact]
    public async Task MissingWorkingDirectoryThrowsGitException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<GitException>(() =>
            new GitRunner().RunAsync(missing, ["--version"], null, CancellationToken.None));
    }
}
