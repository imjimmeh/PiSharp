using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using PiSharp.Cli;
using PiSharp.Cli.Bootstrap;
using PiSharp.Cli.Parsing;
using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Cli.Tests.Bootstrap;

public sealed class CliRuntimeOptionsMapperTests
{
    [Fact]
    public void ContinueMapsToContinueLatestForCwd()
    {
        var options = CliRuntimeOptionsMapper.FromCliArgs(new CliArgs(Continue: true), new SystemExecutionEnv(Environment.CurrentDirectory));

        Assert.True(options.Session!.ContinueLatestForCwd);
    }

    [Fact]
    public void ResumeDoesNotMapToContinueLatestForCwd()
    {
        var options = CliRuntimeOptionsMapper.FromCliArgs(new CliArgs(Resume: true), new SystemExecutionEnv(Environment.CurrentDirectory));

        Assert.False(options.Session!.ContinueLatestForCwd);
    }

    [Fact]
    public async Task StartupResumeSelectionPassesSelectedSessionPathToRuntimeArgs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resume-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var env = new SystemExecutionEnv(root);
        var repo = new JsonlSessionRepo(env, "sessions");
        var current = await repo.CreateAsync(new JsonlSessionCreateOptions(root));
        var other = await repo.CreateAsync(new JsonlSessionCreateOptions(Path.Combine(root, "other")));
        await current.Storage.AppendEntryAsync(UserEntry("current-message", "current"));
        await other.Storage.AppendEntryAsync(UserEntry("other-message", "other"));
        IReadOnlyList<JsonlSessionMetadata>? currentSessions = null;
        IReadOnlyList<JsonlSessionMetadata>? allSessions = null;

        var resolved = await StartupResumeSelector.ResolveAsync(
            new CliArgs(Resume: true, SessionDir: "sessions"),
            AppMode.Interactive,
            env,
            async (loadCurrent, loadAll, _, token) =>
            {
                currentSessions = await loadCurrent(token);
                allSessions = await loadAll(token);
                return other.Metadata;
            },
            CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.False(resolved.Resume);
        Assert.Equal(other.Metadata.Path, resolved.Session);
        Assert.Equal([current.Id], currentSessions!.Select(session => session.Id));
        Assert.Contains(allSessions!, session => session.Id == current.Id);
        Assert.Contains(allSessions!, session => session.Id == other.Id);
    }

    [Fact]
    public async Task StartupResumeSelectionCancellationReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resume-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var env = new SystemExecutionEnv(root);
        var repo = new JsonlSessionRepo(env, "sessions");
        await repo.CreateAsync(new JsonlSessionCreateOptions(root));

        var resolved = await StartupResumeSelector.ResolveAsync(
            new CliArgs(Resume: true, SessionDir: "sessions"),
            AppMode.Interactive,
            env,
            (_, _, _, _) => Task.FromResult<JsonlSessionMetadata?>(null),
            CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public void NoResourcesDisablesResourceLoadedInputsOnly()
    {
        var options = CliRuntimeOptionsMapper.FromCliArgs(new CliArgs(NoResources: true), new SystemExecutionEnv(Environment.CurrentDirectory));

        Assert.NotNull(options.Resources);
        Assert.True(options.Resources!.DisableExtensions);
        Assert.True(options.Resources.DisableSkills);
        Assert.True(options.Resources.DisablePromptTemplates);
        Assert.True(options.Resources.DisableThemes);
        Assert.True(options.Resources.DisableContextFiles);
        Assert.False(options.Tools!.DisableAll);
        Assert.False(options.Tools.DisableBuiltIns);
    }

    private static MessageEntry UserEntry(string id, string text)
        => new() { Id = id, ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User(text) };
}
