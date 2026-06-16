using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class FooterDataProviderPerformanceTests
{
    [Fact]
    public void FooterSnapshotProviderCachesGitBranchBetweenRenders()
    {
        var calls = 0;
        var now = DateTimeOffset.UtcNow;
        var provider = new TuiFooterSnapshotProvider(
            resolveGitBranch: _ =>
            {
                calls++;
                return "main";
            },
            clock: () => now,
            gitBranchCacheDuration: TimeSpan.FromSeconds(30));
        var state = Empty();

        for (var index = 0; index < 100; index++)
        {
            var snapshot = provider.CreateSnapshot(state, "repo");
            Assert.Equal("main", snapshot.GitBranch);
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public void FooterSnapshotProviderRefreshesBranchAfterCacheExpiryOrCwdChange()
    {
        var calls = 0;
        var now = DateTimeOffset.UtcNow;
        var provider = new TuiFooterSnapshotProvider(
            resolveGitBranch: cwd => $"{cwd}-{++calls}",
            clock: () => now,
            gitBranchCacheDuration: TimeSpan.FromSeconds(30));
        var state = Empty();

        Assert.Equal("repo-a-1", provider.CreateSnapshot(state, "repo-a").GitBranch);
        Assert.Equal("repo-a-1", provider.CreateSnapshot(state, "repo-a").GitBranch);
        Assert.Equal(1, calls);

        Assert.Equal("repo-b-2", provider.CreateSnapshot(state, "repo-b").GitBranch);
        Assert.Equal(2, calls);

        now = now.AddSeconds(31);
        Assert.Equal("repo-a-3", provider.CreateSnapshot(state, "repo-a").GitBranch);
        Assert.Equal(3, calls);
    }

    private static TuiRenderState Empty()
        => TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test", ContextWindow: 100), ThinkingLevel.Off, null);
}
