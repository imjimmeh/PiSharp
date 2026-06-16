using PiSharp.Abstractions.Sessions;
using PiSharp.Tui.Interactive.SessionSelection;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class SessionSelectorModelTests
{
    [Fact]
    public void ThreadedRowsNestForkedSessionsUnderParent()
    {
        var parent = Session("parent", "/sessions/parent.jsonl", modifiedAt: "2026-05-01T10:00:00Z", firstMessage: "Parent session");
        var child = Session("child", "/sessions/child.jsonl", modifiedAt: "2026-05-01T11:00:00Z", firstMessage: "Child session", parentPath: "/sessions/parent.jsonl");

        var rows = SessionSelectorModel.BuildRows([child, parent]);

        Assert.Equal(["parent", "child"], rows.Select(row => row.Session.Id));
        Assert.Equal(string.Empty, rows[0].TreePrefix);
        Assert.Equal("└─ ", rows[1].TreePrefix);
    }

    [Fact]
    public void RowsPreferNameOverFirstMessageForDisplayText()
    {
        var named = Session("named", "/sessions/named.jsonl", name: "Release prep", firstMessage: "First message");
        var unnamed = Session("unnamed", "/sessions/unnamed.jsonl", firstMessage: "Fix auth bug");

        var rows = SessionSelectorModel.BuildRows([named, unnamed]);

        Assert.Equal("Release prep", rows.Single(row => row.Session.Id == "named").DisplayText);
        Assert.Equal("Fix auth bug", rows.Single(row => row.Session.Id == "unnamed").DisplayText);
    }

    [Fact]
    public void SearchMatchesIdNameMessageAndCwd()
    {
        var sessions = new[]
        {
            Session("auth-1", "/sessions/auth.jsonl", cwd: "/repo/api", name: "Auth triage", firstMessage: "Investigate login", allMessagesText: "refresh token failure"),
            Session("docs-1", "/sessions/docs.jsonl", cwd: "/repo/docs", firstMessage: "Write guide", allMessagesText: "readme updates")
        };

        Assert.Equal(["auth-1"], SessionSelectorSearch.FilterAndSort(sessions, "auth").Select(session => session.Id));
        Assert.Equal(["auth-1"], SessionSelectorSearch.FilterAndSort(sessions, "token").Select(session => session.Id));
        Assert.Equal(["docs-1"], SessionSelectorSearch.FilterAndSort(sessions, "repo/docs").Select(session => session.Id));
        Assert.Equal(["docs-1"], SessionSelectorSearch.FilterAndSort(sessions, "docs-1").Select(session => session.Id));
    }

    [Fact]
    public void ScopeFilteringKeepsCurrentFolderSeparateFromAllSessions()
    {
        var sessions = new[]
        {
            Session("current", "/sessions/current.jsonl", cwd: "/repo/app"),
            Session("other", "/sessions/other.jsonl", cwd: "/repo/other")
        };

        Assert.Equal(["current"], SessionSelectorModel.FilterScope(sessions, "/repo/app", SessionSelectorScope.Current).Select(session => session.Id));
        Assert.Equal(["current", "other"], SessionSelectorModel.FilterScope(sessions, "/repo/app", SessionSelectorScope.All).Select(session => session.Id));
    }

    [Fact]
    public async Task SessionSelectorIgnoresStaleFilterResults()
    {
        var posts = new Queue<Action>();
        var completions = new Dictionary<string, TaskCompletionSource<SessionSelectorRow[]>>(StringComparer.Ordinal);
        SessionSelectorRow[] applied = [];
        using var scheduler = new SessionSelectorFilterScheduler(
            action => posts.Enqueue(action),
            (_, query, _, _, _) =>
            {
                var completion = new TaskCompletionSource<SessionSelectorRow[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                completions[query] = completion;
                return completion.Task;
            },
            TimeSpan.Zero);

        scheduler.Schedule([Session("alpha", "/sessions/alpha.jsonl")], "a", showCwd: false, DateTimeOffset.UtcNow, rows => applied = rows, CancellationToken.None);
        scheduler.Schedule([Session("alphabet", "/sessions/alphabet.jsonl")], "ab", showCwd: false, DateTimeOffset.UtcNow, rows => applied = rows, CancellationToken.None);
        completions["ab"].SetResult([Row("alphabet")]);
        await WaitForConditionAsync(() => posts.Count == 1, TimeSpan.FromSeconds(1));
        posts.Dequeue()();
        completions["a"].SetResult([Row("alpha")]);
        await Task.Delay(25);
        while (posts.Count > 0) posts.Dequeue()();

        Assert.Equal(["alphabet"], applied.Select(row => row.Session.Id));
    }

    [Fact]
    public async Task SessionSelectorFilterKeepsCanceledTokenUsableUntilWorkCompletes()
    {
        var builderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationSucceeded = false;
        var registrationDisposed = false;
        using var scheduler = new SessionSelectorFilterScheduler(
            _ => { },
            async (_, _, _, _, token) =>
            {
                builderStarted.SetResult();
                await releaseBuilder.Task;
                try
                {
                    using var registration = token.Register(() => { });
                    registrationSucceeded = true;
                }
                catch (ObjectDisposedException)
                {
                    registrationDisposed = true;
                }

                return [];
            },
            TimeSpan.Zero);

        scheduler.Schedule([Session("alpha", "/sessions/alpha.jsonl")], "a", showCwd: false, DateTimeOffset.UtcNow, _ => { }, CancellationToken.None);
        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        scheduler.CancelPending();
        releaseBuilder.SetResult();
        await WaitForConditionAsync(() => registrationSucceeded || registrationDisposed, TimeSpan.FromSeconds(1));

        Assert.True(registrationSucceeded);
        Assert.False(registrationDisposed);
    }

    [Fact]
    public async Task SessionSelectorFilterDoesNotApplyPostedRowsAfterCancelPending()
    {
        var posts = new Queue<Action>();
        var applied = false;
        using var scheduler = new SessionSelectorFilterScheduler(
            action => posts.Enqueue(action),
            (_, _, _, _, _) => Task.FromResult(new[] { Row("alpha") }),
            TimeSpan.Zero);

        scheduler.Schedule([Session("alpha", "/sessions/alpha.jsonl")], "a", showCwd: false, DateTimeOffset.UtcNow, _ => applied = true, CancellationToken.None);
        await WaitForConditionAsync(() => posts.Count == 1, TimeSpan.FromSeconds(1));
        scheduler.CancelPending();
        posts.Dequeue()();

        Assert.False(applied);
    }

    private static JsonlSessionMetadata Session(
        string id,
        string path,
        string cwd = "/repo",
        string? name = null,
        string firstMessage = "First message",
        string allMessagesText = "First message",
        string? parentPath = null,
        string modifiedAt = "2026-05-01T10:00:00Z")
        => new(
            id,
            DateTimeOffset.Parse("2026-05-01T09:00:00Z"),
            cwd,
            path,
            parentPath,
            DateTimeOffset.Parse(modifiedAt),
            1,
            firstMessage,
            allMessagesText,
            name);

    private static SessionSelectorRow Row(string id)
        => new(Session(id, $"/sessions/{id}.jsonl"), string.Empty, id, "1 now", 0, true, []);

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "Expected condition to become true before timeout.");
    }
}
